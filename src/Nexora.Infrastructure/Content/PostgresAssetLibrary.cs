using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Domain.Content;
using Nexora.Domain.Images;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Content;

public sealed class PostgresAssetLibrary(NexoraDbContext db, IBlobStorage storage, TimeProvider clock) : IAssetLibrary
{
    internal static readonly Expression<Func<Asset, AssetSnapshot>> Snapshot = asset => new AssetSnapshot(
        asset.Id, asset.OriginalName, asset.Blob.Size, asset.Blob.DetectedMimeType,
        asset.UploadedAt, asset.IsFavorite, asset.DeletedAt,
        asset.Blob.Image == null ? null : new ImageSnapshot(asset.Blob.Image.State, asset.Blob.Image.Width, asset.Blob.Image.Height,
            asset.Blob.Image.CapturedAtLocal, asset.Blob.Image.CapturedAtUtc, asset.Blob.Image.ProcessedAt,
            asset.Blob.Image.State == ImageProcessingState.Ready, asset.Blob.Image.State == ImageProcessingState.Ready,
            asset.Blob.Image.FailureCode));

    public Task<AssetPage> ListAsync(Guid ownerId, int limit, string? cursor, CancellationToken cancellationToken) =>
        ListAsync(ownerId, limit, cursor, new AssetListQuery(), cancellationToken);

    public Task<AssetPage> ListAsync(Guid ownerId, int limit, string? cursor, AssetListQuery query, CancellationToken cancellationToken) =>
        ListPageAsync(ownerId, limit, cursor, query, AssetListScope.Library, cancellationToken);

    public Task<AssetPage> ListTrashAsync(Guid ownerId, int limit, string? cursor, CancellationToken cancellationToken) =>
        ListPageAsync(ownerId, limit, cursor, new AssetListQuery(), AssetListScope.Trash, cancellationToken);

    private async Task<AssetPage> ListPageAsync(Guid ownerId, int limit, string? cursor, AssetListQuery filters,
        AssetListScope scope, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        filters.Validate();
        var anchor = cursor is null ? null : LibraryCursor.Decode(cursor, ownerId, scope, filters);
        var watermark = anchor?.Watermark ?? clock.GetUtcNow();
        var query = db.Assets.AsNoTracking().Where(asset => asset.OwnerId == ownerId && asset.Blob.State == BlobState.Ready
            && asset.UploadedAt <= watermark);
        query = scope == AssetListScope.Trash
            ? query.Where(asset => asset.DeletedAt != null && asset.DeletedAt <= watermark)
            : query.Where(asset => asset.DeletedAt == null);
        if (filters.ImagesOnly) query = query.Where(asset => asset.Blob.DetectedMimeType.StartsWith("image/"));
        if (filters.IsFavorite is { } favorite) query = query.Where(asset => asset.IsFavorite == favorite);
        var timeline = filters.Sort == AssetSort.Timeline;
        var trash = scope == AssetListScope.Trash;
        var ordered = query.Select(asset => new
        {
            Asset = asset,
            OrderedAt = trash ? asset.DeletedAt!.Value :
                timeline && asset.Blob.Image != null && asset.Blob.Image.State == ImageProcessingState.Ready
                && asset.Blob.Image.ProcessedAt <= watermark && asset.Blob.Image.CapturedAtUtc != null
                    ? asset.Blob.Image.CapturedAtUtc.Value : asset.UploadedAt
        });
        if (anchor is not null) ordered = ordered.Where(row => EF.Functions.LessThan(
            ValueTuple.Create(row.OrderedAt, row.Asset.Id), ValueTuple.Create(anchor.OrderedAt, anchor.AssetId)));

        var rows = await ordered.OrderByDescending(row => row.OrderedAt).ThenByDescending(row => row.Asset.Id)
            .Take(limit + 1).Select(row => new
            {
                row.OrderedAt,
                Snapshot = new AssetSnapshot(row.Asset.Id, row.Asset.OriginalName, row.Asset.Blob.Size, row.Asset.Blob.DetectedMimeType,
                    row.Asset.UploadedAt, row.Asset.IsFavorite, row.Asset.DeletedAt,
                    row.Asset.Blob.Image == null ? null : new ImageSnapshot(row.Asset.Blob.Image.State,
                        row.Asset.Blob.Image.Width, row.Asset.Blob.Image.Height, row.Asset.Blob.Image.CapturedAtLocal,
                        row.Asset.Blob.Image.CapturedAtUtc, row.Asset.Blob.Image.ProcessedAt,
                        row.Asset.Blob.Image.State == ImageProcessingState.Ready, row.Asset.Blob.Image.State == ImageProcessingState.Ready,
                        row.Asset.Blob.Image.FailureCode))
            }).ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var next = more ? LibraryCursor.Encode(ownerId, scope, filters,
            new LibraryPageAnchor(watermark, rows[^1].OrderedAt, rows[^1].Snapshot.Id)) : null;
        return new AssetPage(rows.Select(row => row.Snapshot).ToList(), next);
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
