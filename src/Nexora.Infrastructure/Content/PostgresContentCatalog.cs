using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Domain.Content;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Content;

public sealed class PostgresContentCatalog(NexoraDbContext db, ILogger<PostgresContentCatalog> logger) : IContentCatalog
{
    private static readonly EventId BlobStaged = new(3001, "BlobStaged");
    private static readonly EventId AssetCreated = new(3002, "AssetCreated");
    private static readonly EventId AssetReused = new(3003, "AssetReused");
    private static readonly EventId AssetInTrash = new(3004, "AssetInTrash");

    public async Task<BlobDescriptor> GetOrCreateBlobAsync(Guid ownerId, string sha256, long size,
        string detectedMimeType, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        if (ownerId == Guid.Empty)
            throw new ArgumentException("A content owner is required.", nameof(ownerId));
        var candidate = new Blob(Guid.CreateVersion7(createdAt), sha256, size, detectedMimeType, createdAt);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == ownerId, cancellationToken))
            throw new InvalidOperationException("The content owner does not exist.");

        await AcquireHashLockAsync(sha256, cancellationToken);
        var blob = await db.Blobs.FromSqlInterpolated($"SELECT * FROM \"Blobs\" WHERE \"Sha256\" = {sha256} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (blob is not null)
        {
            if (blob.Size != size)
                throw new StorageIntegrityException();

            await transaction.CommitAsync(cancellationToken);
            return Describe(blob);
        }

        db.Blobs.Add(candidate);
        await db.SaveChangesAsync(cancellationToken);
        // The durable intent is committed before the coordinator publishes any file.
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(BlobStaged, "Blob {BlobId} staged for owner {OwnerId}.", candidate.Id, ownerId);
        return Describe(candidate);
    }

    public async Task<AssetImportResult> CompleteAsync(Guid ownerId, Guid blobId, string originalName,
        DateTimeOffset uploadedAt, CancellationToken cancellationToken)
    {
        // This internal application boundary assumes the coordinator has published
        // or verified the physical blob successfully before requesting completion.
        Asset.ValidateOriginalName(originalName);
        if (uploadedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("The upload timestamp must be UTC.", nameof(uploadedAt));
        if (ownerId == Guid.Empty || blobId == Guid.Empty)
            return Unavailable();

        var hash = await db.Blobs.AsNoTracking().Where(blob => blob.Id == blobId)
            .Select(blob => blob.Sha256).SingleOrDefaultAsync(cancellationToken);
        if (hash is null)
            return Unavailable();

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == ownerId, cancellationToken))
            return Unavailable();

        await AcquireHashLockAsync(hash, cancellationToken);
        var blob = await db.Blobs.FromSqlInterpolated($"SELECT * FROM \"Blobs\" WHERE \"Id\" = {blobId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (blob is null || blob.Sha256 != hash || blob.State is not (BlobState.Staging or BlobState.Ready))
            return Unavailable();

        var existing = await db.Assets.FromSqlInterpolated($"SELECT * FROM \"Assets\" WHERE \"OwnerId\" = {ownerId} AND \"BlobId\" = {blobId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        blob.MarkReady();
        if (existing is not null)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var status = existing.DeletedAt is null ? AssetImportStatus.Reused : AssetImportStatus.AssetInTrash;
            logger.LogInformation(status == AssetImportStatus.Reused ? AssetReused : AssetInTrash,
                "Existing asset {AssetId} for blob {BlobId} and owner {OwnerId} returned with status {Status}.",
                existing.Id, blob.Id, ownerId, status);
            return new AssetImportResult(status, Snapshot(existing, blob));
        }

        var asset = new Asset(Guid.CreateVersion7(uploadedAt), ownerId, blobId, originalName, uploadedAt);
        db.Assets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(AssetCreated, "Asset {AssetId} created for blob {BlobId} and owner {OwnerId}.", asset.Id, blob.Id, ownerId);
        return new AssetImportResult(AssetImportStatus.Created, Snapshot(asset, blob));
    }

    private Task AcquireHashLockAsync(string sha256, CancellationToken cancellationToken)
    {
        // PostgreSQL advisory locks use signed 64-bit keys. The digest prefix is stable
        // across processes; a prefix collision only serializes unrelated content.
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(Convert.FromHexString(sha256.AsSpan(0, 16)));
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);
    }

    private static BlobDescriptor Describe(Blob blob)
        => new(blob.Id, blob.Sha256, blob.Size, blob.DetectedMimeType, blob.StorageKey, blob.State);

    private static AssetSnapshot Snapshot(Asset asset, Blob blob)
        => new(asset.Id, asset.OriginalName, blob.Size, blob.DetectedMimeType, asset.UploadedAt, asset.IsFavorite, asset.DeletedAt);

    private static AssetImportResult Unavailable() => new(AssetImportStatus.BlobUnavailable, null);
}
