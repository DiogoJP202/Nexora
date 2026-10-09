using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Application.Uploads;
using Nexora.Domain.Jobs;
using Nexora.Domain.Content;
using Nexora.Domain.Images;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure.Authentication;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Uploads;

public sealed class PostgresUploadService(NexoraDbContext db, ITemporaryStorage temporaryStorage,
    IStorageUsageReader usageReader, IOptions<UploadOptions> options, TimeProvider clock,
    ILogger<PostgresUploadService> logger) : IUploadService
{
    private const long CapacityLock = 0x4E45584F524143;
    private static readonly EventId UploadCreated = new(4001, "UploadCreated");
    private static readonly EventId ChunkConfirmed = new(4002, "ChunkConfirmed");
    private static readonly EventId UploadQueued = new(4003, "UploadQueued");
    private static readonly EventId UploadCancelled = new(4004, "UploadCancelled");

    public async Task<UploadSnapshot> CreateAsync(Guid ownerId, Guid deviceId, CreateUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;
        if (ownerId == Guid.Empty || deviceId == Guid.Empty || request.ExpectedLength < 0 || request.ClientRequestId == Guid.Empty)
            throw Refuse(400, "invalid_request");
        string? hash;
        try
        {
            Asset.ValidateOriginalName(request.OriginalName);
            hash = request.ExpectedSha256 is null ? null : StreamingContentHash.Normalize(request.ExpectedSha256);
        }
        catch (ArgumentException) { throw Refuse(400, "invalid_request"); }
        var now = clock.GetUtcNow();
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({CapacityLock})", cancellationToken);
        Guid? existingId = request.ClientRequestId is { } requestId
            ? await db.UploadSessions.Where(item => item.OwnerId == ownerId && item.ClientRequestId == requestId)
                .Select(item => (Guid?)item.Id).SingleOrDefaultAsync(cancellationToken)
            : null;
        var uploadId = existingId ?? Guid.CreateVersion7(now);
        await UploadTransactionLock.AcquireAsync(db, uploadId, cancellationToken);
        var owner = await UserTransactionLock.AcquireAsync(db, ownerId, cancellationToken);
        if (owner is null || !await db.Devices.AsNoTracking().AnyAsync(device => device.Id == deviceId
            && device.UserId == ownerId && device.RevokedAt == null, cancellationToken))
            throw Refuse(400, "invalid_device");
        if (existingId is not null)
        {
            var existing = await UploadTransactionLock.RowAsync(db, uploadId, cancellationToken)
                ?? throw Refuse(409, "upload_request_conflict");
            if (existing.OriginalName != request.OriginalName || existing.ExpectedLength != request.ExpectedLength
                || existing.ExpectedSha256 != hash)
                throw Refuse(409, "upload_request_conflict");
            ExpireIfInactive(existing);
            await db.SaveChangesAsync(cancellationToken);
            var repeated = await SnapshotAsync(existing, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return repeated;
        }
        if (request.ExpectedLength > settings.MaximumFileSizeBytes) throw Refuse(413, "file_too_large");
        var upload = new UploadSession(uploadId, ownerId, deviceId, request.OriginalName,
            request.ExpectedLength, hash, settings.ChunkSizeBytes, now, request.ClientRequestId);
        var activeCount = await db.UploadSessions.CountAsync(item => item.OwnerId == ownerId
            && (item.State == UploadState.Open || item.State == UploadState.Finalizing), cancellationToken);
        if (activeCount >= settings.MaximumOpenUploadsPerOwner) throw Refuse(409, "upload_limit_reached");
        var reserved = checked(await db.UploadSessions.SumAsync(item => item.ReservedBytes, cancellationToken)
            + await db.BlobImages.SumAsync(item => item.ReservedBytes, cancellationToken));
        var usage = await usageReader.ReadAsync(cancellationToken);
        if (usage.AvailableBytes < 0 || usage.TemporaryBytes < 0) throw Refuse(503, "storage_unavailable");
        if (reserved > settings.MaximumReservedBytes || upload.ReservedBytes > settings.MaximumReservedBytes - reserved
            || usage.TemporaryBytes > settings.MaximumReservedBytes - reserved - upload.ReservedBytes
            || usage.AvailableBytes < settings.MinimumFreeBytes
            || reserved > usage.AvailableBytes - settings.MinimumFreeBytes
            || upload.ReservedBytes > usage.AvailableBytes - settings.MinimumFreeBytes - reserved)
            throw Refuse(507, "insufficient_storage");
        db.UploadSessions.Add(upload);
        await db.SaveChangesAsync(cancellationToken);
        var snapshot = await SnapshotAsync(upload, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(UploadCreated, "Upload {UploadId} created for owner {OwnerId} and device {DeviceId}.", upload.Id, ownerId, deviceId);
        return snapshot;
    }

    public async Task<UploadSnapshot?> GetAsync(Guid ownerId, Guid uploadId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await UploadTransactionLock.AcquireAsync(db, uploadId, cancellationToken);
        var upload = await UploadTransactionLock.RowAsync(db, uploadId, cancellationToken);
        if (upload is null || upload.OwnerId != ownerId) return null;
        ExpireIfInactive(upload);
        await db.SaveChangesAsync(cancellationToken);
        var snapshot = await SnapshotAsync(upload, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<UploadChunkReceipt> PutChunkAsync(Guid ownerId, Guid uploadId, int number, Stream content,
        string? expectedSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        string? expectedHash;
        try { expectedHash = expectedSha256 is null ? null : StreamingContentHash.Normalize(expectedSha256); }
        catch (ArgumentException) { throw Refuse(400, "invalid_request"); }
        using var timeout = new CancellationTokenSource(options.Value.ChunkTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        TemporaryObjectInfo? staged = null;
        var commitAttempted = false;
        Exception? operationFailure = null;
        db.ChangeTracker.Clear();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await UploadTransactionLock.AcquireAsync(db, uploadId, token);
            var upload = await UploadTransactionLock.RowAsync(db, uploadId, token);
            if (upload is null || upload.OwnerId != ownerId) throw Refuse(404, "upload_not_found");
            if (ExpireIfInactive(upload))
            {
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                throw Refuse(409, "upload_not_open");
            }
            if (upload.State != UploadState.Open) throw Refuse(409, "upload_not_open");
            if (number < 0 || number >= upload.ChunkCount) throw Refuse(400, "invalid_chunk");
            var expectedLength = upload.ExpectedChunkLength(number);
            staged = await temporaryStorage.CreateAsync(content, expectedLength, token);
            if (staged.Length != expectedLength) throw Refuse(400, "chunk_size_mismatch");
            if (expectedHash is not null && staged.Sha256 != expectedHash) throw Refuse(400, "chunk_hash_mismatch");
            var existing = await db.UploadChunks.SingleOrDefaultAsync(chunk => chunk.UploadSessionId == uploadId
                && chunk.Number == number, token);
            var reused = existing is not null;
            if (existing is not null)
            {
                if (existing.Size != staged.Length || existing.Sha256 != staged.Sha256) throw Refuse(409, "chunk_conflict");
                var existingInfo = await temporaryStorage.GetInfoAsync(new TemporaryObjectKey(existing.TemporaryId), token);
                if (existingInfo is null || existingInfo.Length != existing.Size || existingInfo.Sha256 != existing.Sha256)
                    throw Refuse(409, "chunk_integrity_failed");
                await temporaryStorage.DeleteAsync(staged.Key, CancellationToken.None);
                staged = null;
            }
            else
            {
                db.UploadChunks.Add(new UploadChunk(upload.Id, number, staged.Length, staged.Sha256, staged.Key.Id));
            }
            upload.Touch(clock.GetUtcNow());
            await db.SaveChangesAsync(token);
            // A lost acknowledgement may follow a successful COMMIT. In that case the
            // new file must survive because the committed chunk row may reference it.
            commitAttempted = true;
            await transaction.CommitAsync(token);
            logger.LogInformation(ChunkConfirmed, "Chunk {ChunkNumber} confirmed for upload {UploadId}.", number, uploadId);
            return new UploadChunkReceipt(number, expectedLength, existing?.Sha256 ?? staged!.Sha256, reused);
        }
        catch (StorageLimitExceededException exception)
        {
            operationFailure = exception;
            throw Refuse(413, "chunk_too_large");
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            operationFailure = exception;
            throw Refuse(408, "chunk_timeout");
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            if (staged is not null && !commitAttempted)
            {
                try { await temporaryStorage.DeleteAsync(staged.Key, CancellationToken.None); }
                catch (Exception cleanupFailure) when (operationFailure is not null)
                {
                    throw new AggregateException("Chunk ingestion and cleanup failed.", operationFailure, cleanupFailure);
                }
            }
        }
    }

    public async Task<UploadSnapshot?> CompleteAsync(Guid ownerId, Guid uploadId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await UploadTransactionLock.AcquireAsync(db, uploadId, cancellationToken);
        var upload = await UploadTransactionLock.RowAsync(db, uploadId, cancellationToken);
        if (upload is null || upload.OwnerId != ownerId) return null;
        if (ExpireIfInactive(upload))
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw Refuse(409, "upload_not_open");
        }
        if (upload.State is UploadState.Cancelled or UploadState.Expired) throw Refuse(409, "upload_not_open");
        if (upload.State == UploadState.Open)
        {
            var chunks = await db.UploadChunks.AsNoTracking().Where(chunk => chunk.UploadSessionId == uploadId)
                .OrderBy(chunk => chunk.Number).ToListAsync(cancellationToken);
            if (chunks.Count != upload.ChunkCount || chunks.Where((chunk, index) => chunk.Number != index
                || chunk.Size != upload.ExpectedChunkLength(index)).Any())
                throw Refuse(409, "chunks_incomplete");
            var now = clock.GetUtcNow();
            upload.BeginFinalization(now);
            db.BackgroundJobs.Add(new BackgroundJob(Guid.CreateVersion7(now), uploadId, now, options.Value.MaximumJobAttempts));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(UploadQueued, "Upload {UploadId} queued for finalization.", uploadId);
        }
        var snapshot = await SnapshotAsync(upload, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<bool> CancelAsync(Guid ownerId, Guid uploadId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await UploadTransactionLock.AcquireAsync(db, uploadId, cancellationToken);
        var upload = await UploadTransactionLock.RowAsync(db, uploadId, cancellationToken);
        if (upload is null || upload.OwnerId != ownerId) return false;
        if (upload.State == UploadState.Completed) throw Refuse(409, "upload_not_open");
        if (upload.State is UploadState.Open or UploadState.Finalizing)
        {
            upload.Cancel(clock.GetUtcNow());
            var job = await db.BackgroundJobs.SingleOrDefaultAsync(item => item.UploadSessionId == uploadId, cancellationToken);
            job?.Cancel();
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(UploadCancelled, "Upload {UploadId} cancelled for owner {OwnerId}.", uploadId, ownerId);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private bool ExpireIfInactive(UploadSession upload)
    {
        var now = clock.GetUtcNow();
        if (upload.State != UploadState.Open || upload.LastActivityAt > now - options.Value.InactivityExpiration) return false;
        upload.Expire(now);
        return true;
    }

    private async Task<UploadSnapshot> SnapshotAsync(UploadSession upload, CancellationToken cancellationToken)
    {
        var confirmed = upload.State == UploadState.Finalizing && upload.AssemblyTemporaryId is not null
            ? Enumerable.Range(0, upload.ChunkCount).ToArray()
            : await db.UploadChunks.AsNoTracking().Where(chunk => chunk.UploadSessionId == upload.Id)
                .OrderBy(chunk => chunk.Number).Select(chunk => chunk.Number).ToArrayAsync(cancellationToken);
        var job = await db.BackgroundJobs.AsNoTracking().SingleOrDefaultAsync(item => item.UploadSessionId == upload.Id, cancellationToken);
        AssetSnapshot? result = null;
        if (upload.ResultAssetId is not null)
            result = await db.Assets.AsNoTracking().Where(asset => asset.Id == upload.ResultAssetId && asset.OwnerId == upload.OwnerId)
                .Select(asset => new AssetSnapshot(asset.Id, asset.OriginalName, asset.Blob.Size, asset.Blob.DetectedMimeType,
                    asset.UploadedAt, asset.IsFavorite, asset.DeletedAt,
                    asset.Blob.Image == null ? null : new ImageSnapshot(asset.Blob.Image.State, asset.Blob.Image.Width,
                        asset.Blob.Image.Height, asset.Blob.Image.CapturedAtLocal, asset.Blob.Image.CapturedAtUtc,
                        asset.Blob.Image.ProcessedAt, asset.Blob.Image.State == ImageProcessingState.Ready,
                        asset.Blob.Image.State == ImageProcessingState.Ready, asset.Blob.Image.FailureCode))).SingleOrDefaultAsync(cancellationToken);
        return new UploadSnapshot(upload.Id, upload.OriginalName, upload.ExpectedLength, upload.ChunkSize, upload.ChunkCount,
            upload.State, confirmed, upload.CreatedAt, upload.LastActivityAt, result, upload.FailureCode,
            job is null ? null : new UploadOperationSummary(job.Id, job.State, job.Attempts, job.FailureCode), upload.ResultPurgedAt);
    }

    private static UploadOperationException Refuse(int statusCode, string code) => new(statusCode, code);
}
