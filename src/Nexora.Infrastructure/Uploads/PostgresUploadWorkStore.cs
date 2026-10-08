using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Domain.Jobs;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Uploads;

public sealed class PostgresUploadWorkStore(NexoraDbContext db, ITrackedTemporaryStorage temporaryStorage,
    IOptions<UploadOptions> options, TimeProvider clock, ILogger<PostgresUploadWorkStore> logger) : IUploadWorkStore
{
    private static readonly EventId JobClaimed = new(4101, "UploadJobClaimed");
    private static readonly EventId JobFailed = new(4102, "UploadJobFailed");
    private static readonly EventId UploadExpired = new(4103, "UploadExpired");
    private static readonly EventId Cleaned = new(4104, "UploadCleaned");

    public async Task<UploadWorkItem?> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var candidates = await db.BackgroundJobs.AsNoTracking().Where(job =>
                (job.State == BackgroundJobState.Pending && job.NextAttemptAt <= now)
                || (job.State == BackgroundJobState.Running && job.LeaseExpiresAt <= now))
            .OrderBy(job => job.NextAttemptAt).ThenBy(job => job.Id)
            .Select(job => new { job.Id, job.UploadSessionId }).Take(32).ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (!await UploadTransactionLock.TryAcquireAsync(db, candidate.UploadSessionId, cancellationToken)) continue;
            var job = await db.BackgroundJobs.FromSqlInterpolated($"SELECT * FROM \"BackgroundJobs\" WHERE \"Id\" = {candidate.Id} FOR UPDATE SKIP LOCKED")
                .SingleOrDefaultAsync(cancellationToken);
            if (job is null) continue;
            var upload = await UploadTransactionLock.RowAsync(db, candidate.UploadSessionId, cancellationToken);
            now = clock.GetUtcNow();
            if (upload is null || upload.State != UploadState.Finalizing) continue;
            var eligible = (job.State == BackgroundJobState.Pending && job.NextAttemptAt <= now)
                || (job.State == BackgroundJobState.Running && job.LeaseExpiresAt <= now);
            if (!eligible) continue;
            if (job.Attempts >= job.MaximumAttempts)
            {
                job.Fail("attempts_exhausted", retryable: false, now, TimeSpan.Zero);
                upload.Fail("attempts_exhausted", now);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                logger.LogWarning(JobFailed, "Job {JobId} for upload {UploadId} exhausted its attempts.", job.Id, upload.Id);
                continue;
            }
            var token = Guid.NewGuid();
            job.Claim(token, now, options.Value.LeaseDuration);
            db.BackgroundJobAttempts.Add(new BackgroundJobAttempt(token, job.Id, now));
            var chunks = await db.UploadChunks.AsNoTracking().Where(chunk => chunk.UploadSessionId == upload.Id)
                .OrderBy(chunk => chunk.Number)
                .Select(chunk => new UploadChunkWork(chunk.Number, chunk.Size, chunk.Sha256, new TemporaryObjectKey(chunk.TemporaryId)))
                .ToArrayAsync(cancellationToken);
            var previous = await db.BackgroundJobAttempts.AsNoTracking().Where(attempt => attempt.JobId == job.Id && attempt.Id != token)
                .OrderBy(attempt => attempt.CreatedAt).Select(attempt => attempt.Id).ToArrayAsync(cancellationToken);
            var assembly = Assembly(upload);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var lease = new JobLease(job.Id, upload.Id, token, job.LeaseExpiresAt!.Value);
            logger.LogInformation(JobClaimed, "Job {JobId} claimed for upload {UploadId} with attempt {AttemptId}.", job.Id, upload.Id, token);
            return new UploadWorkItem(lease, upload.OwnerId, upload.OriginalName, upload.ExpectedLength, upload.ExpectedSha256,
                upload.ChunkSize, upload.ChunkCount, chunks, assembly, previous.Select(id => new TemporaryObjectKey(id)).ToArray());
        }
        return null;
    }

    public async Task<bool> RenewAsync(JobLease lease, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var guarded = await GuardAsync(lease, cancellationToken);
        if (guarded is null) return false;
        guarded.Value.Job.Renew(lease.Token, clock.GetUtcNow(), options.Value.LeaseDuration);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SaveAssemblyAsync(JobLease lease, TemporaryObjectInfo assembly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var guarded = await GuardAsync(lease, cancellationToken);
        if (guarded is null) return false;
        var upload = guarded.Value.Upload;
        if (assembly.Key.Id != lease.Token || assembly.Length != upload.ExpectedLength
            || (upload.ExpectedSha256 is not null && upload.ExpectedSha256 != assembly.Sha256)
            || !await db.BackgroundJobAttempts.AnyAsync(attempt => attempt.Id == lease.Token && attempt.JobId == lease.JobId, cancellationToken))
            throw new StorageIntegrityException();
        // CreateWithKeyAsync already checked and synchronized this assembly. Rehashing
        // a large file while holding this lock would block renewal of its own lease.
        upload.SaveAssembly(assembly.Key.Id, assembly.Length, assembly.Sha256, assembly.DetectedMimeType, clock.GetUtcNow());
        if (!guarded.Value.Job.HasLease(lease.Token, clock.GetUtcNow())) return false;
        await db.SaveChangesAsync(cancellationToken);
        if (!guarded.Value.Job.HasLease(lease.Token, clock.GetUtcNow())) return false;
        // The processor retains this tracked key on ambiguous commit errors.
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveChunkAsync(JobLease lease, int number, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var guarded = await GuardAsync(lease, cancellationToken);
        if (guarded is null || guarded.Value.Upload.AssemblyTemporaryId is null) return false;
        var chunk = await db.UploadChunks.SingleOrDefaultAsync(item => item.UploadSessionId == lease.UploadId && item.Number == number,
            cancellationToken);
        if (chunk is not null)
        {
            // Keep the row until the physical deletion succeeds, including its directory flush.
            await temporaryStorage.DeleteAttemptAsync(new TemporaryObjectKey(chunk.TemporaryId), preserveCompleted: false, cancellationToken);
            if (!guarded.Value.Job.HasLease(lease.Token, clock.GetUtcNow())) return false;
            db.UploadChunks.Remove(chunk);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> FailAsync(JobLease lease, string failureCode, bool retryable, CancellationToken cancellationToken)
    {
        UploadSession.ValidateFailureCode(failureCode);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var guarded = await GuardAsync(lease, cancellationToken);
        if (guarded is null) return false;
        var now = clock.GetUtcNow();
        var job = guarded.Value.Job;
        job.Fail(failureCode, retryable, now, options.Value.RetryDelay);
        if (job.State == BackgroundJobState.Failed) guarded.Value.Upload.Fail(failureCode, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogWarning(JobFailed, "Job {JobId} for upload {UploadId} changed to {State} with code {FailureCode}.",
            job.Id, lease.UploadId, job.State, failureCode);
        return true;
    }

    public async Task<int> ExpireOpenAsync(CancellationToken cancellationToken)
    {
        var threshold = clock.GetUtcNow() - options.Value.InactivityExpiration;
        var identifiers = await db.UploadSessions.AsNoTracking().Where(upload => upload.State == UploadState.Open
            && upload.LastActivityAt <= threshold).OrderBy(upload => upload.LastActivityAt).Select(upload => upload.Id)
            .Take(128).ToArrayAsync(cancellationToken);
        var count = 0;
        foreach (var identifier in identifiers)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (!await UploadTransactionLock.TryAcquireAsync(db, identifier, cancellationToken)) continue;
            var upload = await UploadTransactionLock.RowAsync(db, identifier, cancellationToken);
            var now = clock.GetUtcNow();
            if (upload is null || upload.State != UploadState.Open || upload.LastActivityAt > now - options.Value.InactivityExpiration) continue;
            upload.Expire(now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(UploadExpired, "Upload {UploadId} expired.", identifier);
            count++;
        }
        return count;
    }

    public async Task<UploadCleanupItem[]> ListCleanupAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0 || limit > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        var now = clock.GetUtcNow();
        var identifiers = await db.UploadSessions.AsNoTracking().Where(upload => upload.State != UploadState.Open
            && upload.State != UploadState.Finalizing && (upload.ReservedBytes > 0 || upload.AssemblyTemporaryId != null
                || upload.Chunks.Any() || (upload.Job != null && db.BackgroundJobAttempts.Any(attempt => attempt.JobId == upload.Job.Id)))
            && (upload.Job == null || upload.Job.LeaseExpiresAt == null || upload.Job.LeaseExpiresAt <= now))
            .OrderBy(upload => upload.LastActivityAt).Select(upload => upload.Id).Take(limit).ToArrayAsync(cancellationToken);
        var result = new List<UploadCleanupItem>();
        foreach (var identifier in identifiers)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (!await UploadTransactionLock.TryAcquireAsync(db, identifier, cancellationToken)) continue;
            var upload = await UploadTransactionLock.RowAsync(db, identifier, cancellationToken);
            if (upload is null || !await CanCleanAsync(upload, cancellationToken)) continue;
            var keys = await CleanupKeysAsync(upload, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            result.Add(new UploadCleanupItem(upload.Id, keys.Select(id => new TemporaryObjectKey(id)).ToArray()));
        }
        return result.ToArray();
    }

    public async Task<bool> FinishCleanupAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await UploadTransactionLock.AcquireAsync(db, uploadId, cancellationToken);
        var upload = await UploadTransactionLock.RowAsync(db, uploadId, cancellationToken);
        if (upload is null || !await CanCleanAsync(upload, cancellationToken)) return false;
        var keys = await CleanupKeysAsync(upload, cancellationToken);
        foreach (var identifier in keys)
            await temporaryStorage.DeleteAttemptAsync(new TemporaryObjectKey(identifier), preserveCompleted: false, cancellationToken);
        // All keys are still durable references until every deletion and fsync above succeeds.
        await db.UploadChunks.Where(chunk => chunk.UploadSessionId == uploadId).ExecuteDeleteAsync(cancellationToken);
        await db.BackgroundJobAttempts.Where(attempt => attempt.Job.UploadSessionId == uploadId).ExecuteDeleteAsync(cancellationToken);
        upload.FinishCleanup();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(Cleaned, "Upload {UploadId} temporary content cleaned and reservation released.", uploadId);
        return true;
    }

    public async Task<HashSet<Guid>> ReferencedTemporaryIdsAsync(CancellationToken cancellationToken)
    {
        var chunkIds = await db.UploadChunks.AsNoTracking().Select(chunk => chunk.TemporaryId).ToArrayAsync(cancellationToken);
        var assemblies = await db.UploadSessions.AsNoTracking().Where(upload => upload.AssemblyTemporaryId != null)
            .Select(upload => upload.AssemblyTemporaryId!.Value).ToArrayAsync(cancellationToken);
        var attempts = await db.BackgroundJobAttempts.AsNoTracking().Select(attempt => attempt.Id).ToArrayAsync(cancellationToken);
        return chunkIds.Concat(assemblies).Concat(attempts).ToHashSet();
    }

    private async Task<(UploadSession Upload, BackgroundJob Job)?> GuardAsync(JobLease lease, CancellationToken cancellationToken)
    {
        await UploadTransactionLock.AcquireAsync(db, lease.UploadId, cancellationToken);
        var upload = await UploadTransactionLock.RowAsync(db, lease.UploadId, cancellationToken);
        if (upload is null || upload.State != UploadState.Finalizing) return null;
        var job = await db.BackgroundJobs.FromSqlInterpolated($"SELECT * FROM \"BackgroundJobs\" WHERE \"Id\" = {lease.JobId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (job is null || job.UploadSessionId != upload.Id || !job.HasLease(lease.Token, clock.GetUtcNow())) return null;
        return (upload, job);
    }

    private async Task<bool> CanCleanAsync(UploadSession upload, CancellationToken cancellationToken)
    {
        if (upload.State is UploadState.Open or UploadState.Finalizing) return false;
        var job = await db.BackgroundJobs.AsNoTracking().SingleOrDefaultAsync(item => item.UploadSessionId == upload.Id, cancellationToken);
        return job is null || job.LeaseExpiresAt is null || job.LeaseExpiresAt <= clock.GetUtcNow();
    }

    private async Task<HashSet<Guid>> CleanupKeysAsync(UploadSession upload, CancellationToken cancellationToken)
    {
        var chunkIds = await db.UploadChunks.AsNoTracking().Where(chunk => chunk.UploadSessionId == upload.Id)
            .Select(chunk => chunk.TemporaryId).ToArrayAsync(cancellationToken);
        var attempts = await db.BackgroundJobAttempts.AsNoTracking().Where(attempt => attempt.Job.UploadSessionId == upload.Id)
            .Select(attempt => attempt.Id).ToArrayAsync(cancellationToken);
        var keys = chunkIds.Concat(attempts).ToHashSet();
        if (upload.AssemblyTemporaryId is { } identifier) keys.Add(identifier);
        return keys;
    }

    private static TemporaryObjectInfo? Assembly(UploadSession upload)
        => upload.AssemblyTemporaryId is { } identifier
            ? new TemporaryObjectInfo(new TemporaryObjectKey(identifier), upload.AssemblyLength!.Value,
                upload.AssemblySha256!, upload.AssemblyMimeType!) : null;
}
