using Microsoft.EntityFrameworkCore;
using Nexora.Application.Content;
using Nexora.Application.Images;
using Nexora.Domain.Content;
using Nexora.Domain.Images;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Images;

public sealed class PostgresAssetDerivatives(NexoraDbContext db, IDerivativeStorage storage) : IAssetDerivatives
{
    public async Task<AssetDerivativeStream?> OpenAsync(Guid ownerId, Guid assetId, DerivativeKind kind, CancellationToken cancellationToken)
    {
        var image = await db.Assets.AsNoTracking().Where(asset => asset.OwnerId == ownerId && asset.Id == assetId
                && asset.DeletedAt == null && asset.Blob.State == BlobState.Ready)
            .Select(asset => asset.Blob.Image).SingleOrDefaultAsync(cancellationToken);
        if (image is null || image.State != ImageProcessingState.Ready) return null;
        var key = new DerivativeKey(image.DerivativeGenerationId!.Value, kind);
        var length = kind == DerivativeKind.Thumbnail ? image.ThumbnailLength : image.PreviewLength;
        var hash = kind == DerivativeKind.Thumbnail ? image.ThumbnailSha256 : image.PreviewSha256;
        Stream? content = null;
        try
        {
            var info = await storage.GetInfoAsync(key, cancellationToken);
            if (info is null || info.Length != length || info.Sha256 != hash) throw new AssetContentUnavailableException();
            content = await storage.OpenReadAsync(key, cancellationToken);
            return new AssetDerivativeStream(content, key.GenerationId, image.ProcessedAt!.Value, length!.Value);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            if (content is not null) await content.DisposeAsync();
            throw new AssetContentUnavailableException();
        }
    }
}
