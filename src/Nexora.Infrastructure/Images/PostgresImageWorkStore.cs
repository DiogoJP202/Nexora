using System.Data;
using System.Security.Cryptography;
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

namespace Nexora.Infrastructure.Images;

public sealed class PostgresImageWorkStore(NexoraDbContext db, IDerivativeStorage derivatives,
    IStorageUsageReader usageReader, IOptions<ImageOptions> options, IOptions<UploadOptions> uploadOptions,
    TimeProvider clock, ILogger<PostgresImageWorkStore> logger) : IImageWorkStore
{
    private const long CapacityLock = 0x4E45584F524143;
    private static readonly EventId Claimed = new(5001, "ImageJobClaimed");
    private static readonly EventId Failed = new(5002, "ImageJobFailed");
    private static readonly EventId Completed = new(5003, "ImageJobCompleted");

    public async Task<ImageWorkItem?> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var candidates = await db.BackgroundJobs.AsNoTracking().Where(job => job.Kind == "ProcessImage" && (
                (job.State == BackgroundJobState.Pending && job.NextAttemptAt <= now)
                || (job.State == BackgroundJobState.Running && job.LeaseExpiresAt <= now)))
            .OrderBy(job => job.NextAttemptAt).ThenBy(job => job.Id)
            .Select(job => new { job.Id, job.BlobId }).Take(32).ToArrayAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await CapacityAsync(cancellationToken);
            var blob = await BlobRowAsync(candidate.BlobId!.Value, cancellationToken);
            var image = await ImageRowAsync(candidate.BlobId.Value, cancellationToken);
            var job = await db.BackgroundJobs.FromSqlInterpolated($"SELECT * FROM \"BackgroundJobs\" WHERE \"Id\" = {candidate.Id} FOR UPDATE SKIP LOCKED")
                .SingleOrDefaultAsync(cancellationToken);
            now = clock.GetUtcNow();
            if (blob is null || image is null || job is null || job.Kind != "ProcessImage" || job.BlobId != blob.Id
                || image.State is ImageProcessingState.Ready or ImageProcessingState.Failed) continue;
            if (!((job.State == BackgroundJobState.Pending && job.NextAttemptAt <= now)
                || (job.State == BackgroundJobState.Running && job.LeaseExpiresAt <= now))) continue;
            if (blob.State != BlobState.Ready || job.Attempts >= job.MaximumAttempts)
            {
                var code = blob.State != BlobState.Ready ? "image_original_unavailable" : "attempts_exhausted";
                job.Fail(code, false, now, TimeSpan.Zero);
                image.Fail(code, false);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                logger.LogWarning(Failed, "Image job {JobId} for blob {BlobId} failed with code {FailureCode}.", job.Id, blob.Id, code);
                continue;
            }

            var reservation = Math.Max(image.ReservedBytes, checked(2L * options.Value.MaximumDerivativeBytes));
            var additional = reservation - image.ReservedBytes;
            var allReserved = checked(await db.UploadSessions.SumAsync(upload => upload.ReservedBytes, cancellationToken)
                + await db.BlobImages.SumAsync(item => item.ReservedBytes, cancellationToken));
            var usage = await usageReader.ReadAsync(cancellationToken);
            var budget = uploadOptions.Value;
            var fits = usage.AvailableBytes >= budget.MinimumFreeBytes && usage.TemporaryBytes >= 0
                && allReserved <= budget.MaximumReservedBytes && additional <= budget.MaximumReservedBytes - allReserved
                && usage.TemporaryBytes <= budget.MaximumReservedBytes - allReserved - additional
                && allReserved <= usage.AvailableBytes - budget.MinimumFreeBytes
                && additional <= usage.AvailableBytes - budget.MinimumFreeBytes - allReserved;
            if (!fits)
            {
                // Capacity waits do not consume processing attempts. The intent remains
                // visible and the original remains downloadable while disk admission waits.
                job.Fail("insufficient_storage", true, now, options.Value.RetryDelay);
                image.Fail("insufficient_storage", true);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                continue;
            }
            var token = Guid.NewGuid();
            job.Claim(token, now, options.Value.LeaseDuration);
            image.BeginProcessing(reservation);
            db.BackgroundJobAttempts.Add(new BackgroundJobAttempt(token, job.Id, now));
            var previous = await db.BackgroundJobAttempts.AsNoTracking().Where(attempt => attempt.JobId == job.Id && attempt.Id != token)
                .OrderBy(attempt => attempt.CreatedAt).Select(attempt => attempt.Id).ToArrayAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(Claimed, "Image job {JobId} claimed for blob {BlobId} with attempt {AttemptId}.", job.Id, blob.Id, token);
            return new ImageWorkItem(new ImageJobLease(job.Id, blob.Id, token, job.LeaseExpiresAt!.Value),
                new BlobDescriptor(blob.Id, blob.Sha256, blob.Size, blob.DetectedMimeType, blob.StorageKey, blob.State), previous);
        }
        return null;
    }

    public async Task<bool> RenewAsync(ImageJobLease lease, CancellationToken cancellationToken)
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

    public async Task<bool> CompleteAsync(ImageJobLease lease, RenderedImage image, DerivativeInfo thumbnail,
        DerivativeInfo preview, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var limit = options.Value.MaximumDerivativeBytes;
        if (image.Thumbnail.Length <= 0 || image.Preview.Length <= 0 || image.Thumbnail.Length > limit || image.Preview.Length > limit
            || thumbnail.Length != image.Thumbnail.LongLength || preview.Length != image.Preview.LongLength
            || thumbnail.Sha256 != Digest(image.Thumbnail) || preview.Sha256 != Digest(image.Preview))
            throw new ImageProcessingException("image_derivative_integrity_failed");
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var guarded = await GuardAsync(lease, cancellationToken);
        if (guarded is null) return false;
        var state = guarded.Value.Image;
        var job = guarded.Value.Job;
        var expiration = job.LeaseExpiresAt!.Value;
        state.Complete(lease.Token, image.Width, image.Height, image.CapturedAtLocal, image.CapturedAtUtc,
            thumbnail.Length, thumbnail.Sha256, preview.Length, preview.Sha256, clock.GetUtcNow());
        job.Succeed();
        await db.SaveChangesAsync(cancellationToken);
        if (expiration <= clock.GetUtcNow()) return false;
        // A lost COMMIT acknowledgement leaves this attempt recorded. Cleanup consults
        // the database before deleting either published derivative generation.
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(Completed, "Image job {JobId} completed for blob {BlobId}.", lease.JobId, lease.BlobId);
        return true;
    }

    public async Task<bool> FailAsync(ImageJobLease lease, string code, bool retryable, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var guarded = await GuardAsync(lease, cancellationToken, requireReadyBlob: false);
        if (guarded is null) return false;
        guarded.Value.Job.Fail(code, retryable, clock.GetUtcNow(), options.Value.RetryDelay);
        guarded.Value.Image.Fail(code, guarded.Value.Job.State == BackgroundJobState.Pending);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogWarning(Failed, "Image job {JobId} for blob {BlobId} changed to {State} with code {FailureCode}.",
            lease.JobId, lease.BlobId, guarded.Value.Job.State, code);
        return true;
    }

    public async Task<ImageCleanupItem[]> ListCleanupAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        var now = clock.GetUtcNow();
        var candidates = await db.BackgroundJobs.AsNoTracking().Where(job => job.Kind == "ProcessImage"
            && (job.State == BackgroundJobState.Succeeded || job.State == BackgroundJobState.Failed || job.State == BackgroundJobState.Cancelled)
            && (job.LeaseExpiresAt == null || job.LeaseExpiresAt <= now)
            && (db.BackgroundJobAttempts.Any(attempt => attempt.JobId == job.Id)
                || db.BlobImages.Any(image => image.BlobId == job.BlobId && image.ReservedBytes > 0)))
            .OrderBy(job => job.CreatedAt).Select(job => new { job.Id, job.BlobId }).Take(limit).ToArrayAsync(cancellationToken);
        var result = new List<ImageCleanupItem>();
        foreach (var candidate in candidates)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var state = await CleanupGuardAsync(candidate.Id, candidate.BlobId!.Value, cancellationToken);
            if (state is null) continue;
            var attempts = await AttemptIdsAsync(candidate.Id, cancellationToken);
            var published = state.Value.Image.State == ImageProcessingState.Ready ? state.Value.Image.DerivativeGenerationId : null;
            await transaction.CommitAsync(cancellationToken);
            result.Add(new ImageCleanupItem(candidate.Id, candidate.BlobId.Value, published, attempts));
        }
        return result.ToArray();
    }

    public async Task<bool> FinishCleanupAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var blobId = await db.BackgroundJobs.AsNoTracking().Where(job => job.Id == jobId && job.Kind == "ProcessImage")
            .Select(job => job.BlobId).SingleOrDefaultAsync(cancellationToken);
        if (blobId is null) return false;
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await CapacityAsync(cancellationToken);
        var state = await CleanupGuardAsync(jobId, blobId.Value, cancellationToken);
        if (state is null) return false;
        var published = state.Value.Image.State == ImageProcessingState.Ready ? state.Value.Image.DerivativeGenerationId : null;
        var attempts = await AttemptIdsAsync(jobId, cancellationToken);
        foreach (var attempt in attempts)
            await derivatives.CleanAttemptAsync(attempt, preservePublished: published == attempt, cancellationToken);
        await db.BackgroundJobAttempts.Where(attempt => attempt.JobId == jobId).ExecuteDeleteAsync(cancellationToken);
        state.Value.Image.FinishCleanup();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task BackfillAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        var candidates = await db.Blobs.AsNoTracking().Where(blob => blob.State == BlobState.Ready && blob.Image == null
            && (blob.DetectedMimeType == "image/jpeg" || blob.DetectedMimeType == "image/png" || blob.DetectedMimeType == "image/webp"))
            .OrderBy(blob => blob.CreatedAt).Select(blob => blob.Id).Take(limit).ToArrayAsync(cancellationToken);
        foreach (var blobId in candidates)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var blob = await BlobRowAsync(blobId, cancellationToken);
            if (blob is null || blob.State != BlobState.Ready || !BlobImage.Supports(blob.DetectedMimeType)
                || await db.BlobImages.AnyAsync(image => image.BlobId == blobId, cancellationToken)) continue;
            var now = clock.GetUtcNow();
            db.BlobImages.Add(new BlobImage(blobId, now));
            db.BackgroundJobs.Add(BackgroundJob.ForImage(Guid.CreateVersion7(now), blobId, now, options.Value.MaximumJobAttempts));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private async Task<(BlobImage Image, BackgroundJob Job)?> GuardAsync(ImageJobLease lease, CancellationToken cancellationToken,
        bool requireReadyBlob = true)
    {
        var blob = await BlobRowAsync(lease.BlobId, cancellationToken);
        var image = await ImageRowAsync(lease.BlobId, cancellationToken);
        var job = await JobRowAsync(lease.JobId, cancellationToken);
        if (blob is null || (requireReadyBlob && blob.State != BlobState.Ready)
            || image is null || image.State != ImageProcessingState.Processing || job is null
            || job.Kind != "ProcessImage" || job.BlobId != lease.BlobId || !job.HasLease(lease.Token, clock.GetUtcNow())) return null;
        return (image, job);
    }

    private async Task<(BlobImage Image, BackgroundJob Job)?> CleanupGuardAsync(Guid jobId, Guid blobId, CancellationToken cancellationToken)
    {
        await BlobRowAsync(blobId, cancellationToken);
        var image = await ImageRowAsync(blobId, cancellationToken);
        var job = await JobRowAsync(jobId, cancellationToken);
        if (image is null || job is null || job.Kind != "ProcessImage" || job.BlobId != blobId
            || image.State is not (ImageProcessingState.Ready or ImageProcessingState.Failed)
            || job.State is not (BackgroundJobState.Succeeded or BackgroundJobState.Failed or BackgroundJobState.Cancelled)
            || (job.LeaseExpiresAt is { } expiration && expiration > clock.GetUtcNow())) return null;
        return (image, job);
    }

    private Task CapacityAsync(CancellationToken cancellationToken)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({CapacityLock})", cancellationToken);
    private Task<Blob?> BlobRowAsync(Guid id, CancellationToken cancellationToken)
        => db.Blobs.FromSqlInterpolated($"SELECT * FROM \"Blobs\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
    private Task<BlobImage?> ImageRowAsync(Guid blobId, CancellationToken cancellationToken)
        => db.BlobImages.FromSqlInterpolated($"SELECT * FROM \"BlobImages\" WHERE \"BlobId\" = {blobId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
    private Task<BackgroundJob?> JobRowAsync(Guid id, CancellationToken cancellationToken)
        => db.BackgroundJobs.FromSqlInterpolated($"SELECT * FROM \"BackgroundJobs\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
    private Task<Guid[]> AttemptIdsAsync(Guid jobId, CancellationToken cancellationToken)
        => db.BackgroundJobAttempts.AsNoTracking().Where(attempt => attempt.JobId == jobId).Select(attempt => attempt.Id).ToArrayAsync(cancellationToken);
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
