using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Content;
using Nexora.Application.Images;
using Nexora.Application.Storage;
using Nexora.Domain.Content;
using Nexora.Domain.Images;
using Nexora.Domain.Jobs;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Content;

public sealed class PostgresContentMaintenance(NexoraDbContext db, IBlobStorage originals,
    IDerivativeStorage derivatives, IOptions<LibraryOptions> options, TimeProvider clock,
    ILogger<PostgresContentMaintenance> logger) : IContentMaintenance
{
    public async Task<int> PurgeExpiredAssetsAsync(int limit, CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        var cutoff = clock.GetUtcNow() - options.Value.TrashRetention;
        var candidates = await db.Assets.AsNoTracking().Where(asset => asset.DeletedAt <= cutoff)
            .OrderBy(asset => asset.DeletedAt).ThenBy(asset => asset.Id)
            .Select(asset => new { asset.Id, asset.BlobId, asset.Blob.Sha256 }).Take(limit).ToArrayAsync(cancellationToken);
        var purged = 0;
        foreach (var candidate in candidates)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await HashAsync(candidate.Sha256, cancellationToken);
            var blob = await BlobAsync(candidate.BlobId, cancellationToken);
            if (blob is null) continue;
            var asset = await db.Assets.FromSqlInterpolated($"SELECT * FROM \"Assets\" WHERE \"Id\" = {candidate.Id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            var now = clock.GetUtcNow();
            if (asset is null || asset.BlobId != blob.Id || asset.DeletedAt is null
                || asset.DeletedAt > now - options.Value.TrashRetention) continue;
            // Completed upload rows retain their operation history after the result is purged.
            // Active upload finalization does not reference a result yet, so it cannot
            // invert its upload -> hash -> Blob lock order here.
            var uploads = await db.UploadSessions.FromSqlInterpolated(
                $"SELECT * FROM \"UploadSessions\" WHERE \"ResultAssetId\" = {asset.Id} ORDER BY \"Id\" FOR UPDATE")
                .ToArrayAsync(cancellationToken);
            foreach (var upload in uploads) upload.MarkResultPurged(now);
            await db.SaveChangesAsync(cancellationToken);
            db.Assets.Remove(asset);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            purged++;
            logger.LogInformation("Asset {AssetId} for owner {OwnerId} purged from blob {BlobId}.", asset.Id, asset.OwnerId, blob.Id);
        }
        return purged;
    }

    public async Task<int> CollectBlobsAsync(int limit, CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        var cutoff = clock.GetUtcNow() - options.Value.UnreferencedBlobGracePeriod;
        var candidates = await db.Blobs.AsNoTracking().Where(blob =>
                (blob.State == BlobState.Deleting || (blob.State == BlobState.Ready && blob.CreatedAt <= cutoff))
                && !db.Assets.Any(asset => asset.BlobId == blob.Id))
            .OrderBy(blob => blob.State == BlobState.Deleting ? 0 : 1).ThenBy(blob => blob.CreatedAt).ThenBy(blob => blob.Id)
            .Select(blob => new { blob.Id, blob.Sha256 }).Take(limit).ToArrayAsync(cancellationToken);
        var collected = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                if (!await MarkDeletingAsync(candidate.Id, candidate.Sha256, cancellationToken)) continue;
                if (await FinishDeletingAsync(candidate.Id, candidate.Sha256, cancellationToken)) collected++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // The durable Deleting row and generation/attempt references survive every
                // physical failure, including a lost acknowledgement after an unlink.
                logger.LogWarning("Blob {BlobId} collection deferred with {FailureType}.", candidate.Id, failure.GetType().Name);
            }
        }
        return collected;
    }

    private async Task<bool> MarkDeletingAsync(Guid id, string hash, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await HashAsync(hash, cancellationToken);
        // Never wait for a processor while holding the Blob row it needs to complete.
        if (!await BlobProcessingLock.TryDeleteAsync(db, id, cancellationToken)) return false;
        var blob = await BlobAsync(id, cancellationToken);
        var now = clock.GetUtcNow();
        if (blob is null || blob.Sha256 != hash || blob.State == BlobState.Staging
            || (blob.State == BlobState.Ready && blob.CreatedAt > now - options.Value.UnreferencedBlobGracePeriod)
            || await db.Assets.AnyAsync(asset => asset.BlobId == id, cancellationToken)) return false;
        var image = await ImageAsync(id, cancellationToken);
        var job = await JobAsync(id, cancellationToken);
        if (job?.LeaseExpiresAt > now) return false;
        if (job?.State is BackgroundJobState.Pending or BackgroundJobState.Running)
        {
            job.Fail("image_original_unavailable", false, now, TimeSpan.Zero);
            if (image?.State is ImageProcessingState.Pending or ImageProcessingState.Processing)
                image.Fail("image_original_unavailable", false);
        }
        blob.MarkDeleting();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> FinishDeletingAsync(Guid id, string hash, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await HashAsync(hash, cancellationToken);
        if (!await BlobProcessingLock.TryDeleteAsync(db, id, cancellationToken)) return false;
        var blob = await BlobAsync(id, cancellationToken);
        if (blob is null || blob.Sha256 != hash || blob.State != BlobState.Deleting
            || await db.Assets.AnyAsync(asset => asset.BlobId == id, cancellationToken)) return false;
        var image = await ImageAsync(id, cancellationToken);
        var job = await JobAsync(id, cancellationToken);
        if (job?.LeaseExpiresAt > clock.GetUtcNow() || job?.State is BackgroundJobState.Pending or BackgroundJobState.Running) return false;
        var attempts = job is null ? [] : await db.BackgroundJobAttempts.AsNoTracking()
            .Where(attempt => attempt.JobId == job.Id).Select(attempt => attempt.Id).ToArrayAsync(cancellationToken);
        var generations = attempts.AsEnumerable();
        if (image?.DerivativeGenerationId is { } published) generations = generations.Append(published);
        foreach (var generation in generations.Distinct())
            await derivatives.CleanAttemptAsync(generation, preservePublished: false, cancellationToken);
        await originals.DeleteAsync(blob.StorageKey, cancellationToken);
        // Cascade removes image jobs/attempts and reservations only after every file
        // deletion was synchronized. Distinct physical UUIDs fence later reuploads.
        db.Blobs.Remove(blob);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Blob {BlobId} collected.", id);
        return true;
    }

    private Task HashAsync(string hash, CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock({BinaryPrimitives.ReadInt64BigEndian(Convert.FromHexString(hash.AsSpan(0, 16)))})", cancellationToken);
    private Task<Blob?> BlobAsync(Guid id, CancellationToken cancellationToken) => db.Blobs
        .FromSqlInterpolated($"SELECT * FROM \"Blobs\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
    private Task<BlobImage?> ImageAsync(Guid id, CancellationToken cancellationToken) => db.BlobImages
        .FromSqlInterpolated($"SELECT * FROM \"BlobImages\" WHERE \"BlobId\" = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
    private Task<BackgroundJob?> JobAsync(Guid id, CancellationToken cancellationToken) => db.BackgroundJobs
        .FromSqlInterpolated($"SELECT * FROM \"BackgroundJobs\" WHERE \"BlobId\" = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
    }
}
