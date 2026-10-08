using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Content;
using Nexora.Domain.Content;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Content;

public sealed class PostgresAssetLifecycle(NexoraDbContext db, TimeProvider clock, ILogger<PostgresAssetLifecycle> logger) : IAssetLifecycle
{
    private static readonly EventId AssetUpdated = new(6001, "AssetUpdated");
    private static readonly EventId AssetTrashed = new(6002, "AssetTrashed");
    private static readonly EventId AssetRestored = new(6003, "AssetRestored");

    public Task<AssetSnapshot?> UpdateAsync(Guid ownerId, Guid assetId, AssetUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        update.Validate();
        return MutateAsync(ownerId, assetId, asset =>
        {
            if (update.OriginalName is not null) asset.Rename(update.OriginalName);
            if (update.IsFavorite is { } favorite) asset.SetFavorite(favorite);
        }, requireActive: true, requireReady: true, AssetUpdated, cancellationToken);
    }

    public async Task<bool> MoveToTrashAsync(Guid ownerId, Guid assetId, CancellationToken cancellationToken) =>
        await MutateAsync(ownerId, assetId, asset => asset.MoveToTrash(clock.GetUtcNow()),
            requireActive: false, requireReady: false, AssetTrashed, cancellationToken) is not null;

    public Task<AssetSnapshot?> RestoreAsync(Guid ownerId, Guid assetId, CancellationToken cancellationToken) =>
        MutateAsync(ownerId, assetId, asset => asset.Restore(), requireActive: false, requireReady: true, AssetRestored, cancellationToken);

    private async Task<AssetSnapshot?> MutateAsync(Guid ownerId, Guid assetId, Action<Asset> change,
        bool requireActive, bool requireReady, EventId operation, CancellationToken cancellationToken)
    {
        if (ownerId == Guid.Empty || assetId == Guid.Empty) return null;
        var blobId = await db.Assets.AsNoTracking().Where(asset => asset.Id == assetId && asset.OwnerId == ownerId)
            .Select(asset => (Guid?)asset.BlobId).SingleOrDefaultAsync(cancellationToken);
        if (blobId is null) return null;
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Catalog completion and collection use the same Blob -> Asset lock order.
        // Re-read both identities after waiting: a purge may have removed the item.
        var blob = await db.Blobs.FromSqlInterpolated($"SELECT * FROM \"Blobs\" WHERE \"Id\" = {blobId.Value} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (blob is null || (requireReady && blob.State != BlobState.Ready)) return null;
        var asset = await db.Assets.FromSqlInterpolated($"SELECT * FROM \"Assets\" WHERE \"Id\" = {assetId} AND \"OwnerId\" = {ownerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (asset is null || asset.BlobId != blob.Id || (requireActive && asset.DeletedAt is not null)) return null;
        change(asset);
        await db.SaveChangesAsync(cancellationToken);
        var snapshot = await db.Assets.AsNoTracking().Where(item => item.Id == assetId)
            .Select(PostgresAssetLibrary.Snapshot).SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(operation, "Asset {AssetId} for owner {OwnerId} completed operation {Operation}.",
            asset.Id, ownerId, operation.Name);
        return snapshot;
    }
}
