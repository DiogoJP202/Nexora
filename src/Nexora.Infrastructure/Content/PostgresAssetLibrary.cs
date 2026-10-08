using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Domain.Content;
using Nexora.Domain.Images;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Content;

public sealed class PostgresAssetLibrary(NexoraDbContext db, IBlobStorage storage) : IAssetLibrary
{
    private static readonly Expression<Func<Asset, AssetSnapshot>> Snapshot = asset => new AssetSnapshot(
        asset.Id, asset.OriginalName, asset.Blob.Size, asset.Blob.DetectedMimeType,
        asset.UploadedAt, asset.IsFavorite, asset.DeletedAt,
        asset.Blob.Image == null ? null : new ImageSnapshot(asset.Blob.Image.State, asset.Blob.Image.Width, asset.Blob.Image.Height,
            asset.Blob.Image.CapturedAtLocal, asset.Blob.Image.CapturedAtUtc, asset.Blob.Image.ProcessedAt,
            asset.Blob.Image.State == ImageProcessingState.Ready, asset.Blob.Image.State == ImageProcessingState.Ready,
            asset.Blob.Image.FailureCode));

    public async Task<AssetPage> ListAsync(Guid ownerId, int limit, string? cursor, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var query = AccessibleAssets(ownerId);
        if (cursor is not null)
        {
            var anchor = AssetCursor.Decode(cursor, ownerId);
            query = query.Where(asset => EF.Functions.LessThan(
                ValueTuple.Create(asset.UploadedAt, asset.Id), ValueTuple.Create(anchor.UploadedAt, anchor.AssetId)));
        }

        var page = await query.OrderByDescending(asset => asset.UploadedAt).ThenByDescending(asset => asset.Id)
            .Take(limit + 1).Select(Snapshot).ToListAsync(cancellationToken);
        var more = page.Count > limit;
        if (more) page.RemoveAt(page.Count - 1);
        var next = more ? AssetCursor.Encode(ownerId, page[^1].UploadedAt, page[^1].Id) : null;
        return new AssetPage(page, next);
    }

    public Task<AssetSnapshot?> FindAsync(Guid ownerId, Guid assetId, CancellationToken cancellationToken) =>
        AccessibleAssets(ownerId).Where(asset => asset.Id == assetId).Select(Snapshot).SingleOrDefaultAsync(cancellationToken);

    public async Task<AssetContentStream?> OpenContentAsync(Guid ownerId, Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await AccessibleAssets(ownerId).Where(asset => asset.Id == assetId)
            .Include(item => item.Blob).ThenInclude(blob => blob.Image).SingleOrDefaultAsync(cancellationToken);
        if (asset is null) return null;

        Stream? content = null;
        try
        {
            var info = await storage.GetInfoAsync(asset.Blob.StorageKey, cancellationToken);
            if (info is null || info.Length != asset.Blob.Size) throw new AssetContentUnavailableException();
            content = await storage.OpenReadAsync(asset.Blob.StorageKey, cancellationToken);
            var snapshot = new AssetSnapshot(asset.Id, asset.OriginalName, asset.Blob.Size,
                asset.Blob.DetectedMimeType, asset.UploadedAt, asset.IsFavorite, asset.DeletedAt,
                asset.Blob.Image is { } image ? new ImageSnapshot(image.State, image.Width, image.Height, image.CapturedAtLocal,
                    image.CapturedAtUtc, image.ProcessedAt, image.State == ImageProcessingState.Ready,
                    image.State == ImageProcessingState.Ready, image.FailureCode) : null);
            return new AssetContentStream(snapshot, content, asset.BlobId);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            if (content is not null) await content.DisposeAsync();
            throw new AssetContentUnavailableException();
        }
    }

    private IQueryable<Asset> AccessibleAssets(Guid ownerId) => db.Assets.AsNoTracking()
        .Where(asset => asset.OwnerId == ownerId && asset.DeletedAt == null && asset.Blob.State == BlobState.Ready);
}
