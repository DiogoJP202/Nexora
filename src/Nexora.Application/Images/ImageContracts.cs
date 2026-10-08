using Nexora.Application.Content;
using Nexora.Domain.Content;

namespace Nexora.Application.Images;

public enum DerivativeKind { Thumbnail, Preview }

public readonly record struct DerivativeKey
{
    public DerivativeKey(Guid generationId, DerivativeKind kind)
    {
        if (generationId == Guid.Empty || !Enum.IsDefined(kind)) throw new ArgumentException("Invalid derivative key.");
        GenerationId = generationId;
        Kind = kind;
    }
    public Guid GenerationId { get; }
    public DerivativeKind Kind { get; }
    public override string ToString()
    {
        if (GenerationId == Guid.Empty || !Enum.IsDefined(Kind)) throw new ArgumentException("Invalid derivative key.");
        var id = GenerationId.ToString("N");
        return $"{(Kind == DerivativeKind.Thumbnail ? "thumbnails" : "previews")}/{id[..2]}/{id[2..4]}/{id}.png";
    }
}

public sealed record DerivativeInfo(long Length, string Sha256);
public interface IDerivativeStorage
{
    Task<DerivativeInfo> PublishAsync(DerivativeKey key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(DerivativeKey key, CancellationToken cancellationToken);
    Task<DerivativeInfo?> GetInfoAsync(DerivativeKey key, CancellationToken cancellationToken);
    Task CleanAttemptAsync(Guid generationId, bool preservePublished, CancellationToken cancellationToken);
}

public sealed record ImageRenderParameters(long ExpectedLength, string ExpectedSha256, string ExpectedMimeType,
    long MaximumInputBytes, long MaximumPixels, long MaximumDecodedBytes, int MaximumDimension,
    int ThumbnailSize, int PreviewSize, int MaximumDerivativeBytes);
public sealed record RenderedImage(int Width, int Height, DateTime? CapturedAtLocal, DateTimeOffset? CapturedAtUtc,
    byte[] Thumbnail, byte[] Preview);
public sealed record ImageRenderResponse(RenderedImage? Image, string? FailureCode);
public interface IImageRenderer
{
    Task<RenderedImage> RenderAsync(BlobDescriptor blob, Stream original, CancellationToken cancellationToken);
}
public sealed class ImageProcessingException(string code, bool retryable = false) : Exception("Image processing failed.")
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed record ImageJobLease(Guid JobId, Guid BlobId, Guid Token, DateTimeOffset ExpiresAt);
public sealed record ImageWorkItem(ImageJobLease Lease, BlobDescriptor Blob, Guid[] PreviousAttempts);
public sealed record ImageCleanupItem(Guid JobId, Guid BlobId, Guid? PublishedGeneration, Guid[] Attempts);
public interface IImageWorkStore
{
    Task<ImageWorkItem?> ClaimAsync(CancellationToken cancellationToken);
    Task<bool> RenewAsync(ImageJobLease lease, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(ImageJobLease lease, RenderedImage image, DerivativeInfo thumbnail, DerivativeInfo preview, CancellationToken cancellationToken);
    Task<bool> FailAsync(ImageJobLease lease, string code, bool retryable, CancellationToken cancellationToken);
    Task<ImageCleanupItem[]> ListCleanupAsync(int limit, CancellationToken cancellationToken);
    Task<bool> FinishCleanupAsync(Guid jobId, CancellationToken cancellationToken);
    Task BackfillAsync(int limit, CancellationToken cancellationToken);
}
public interface IImageJobProcessor
{
    Task ProcessAsync(ImageWorkItem work, CancellationToken cancellationToken);
}
public sealed record AssetDerivativeStream(Stream Content, Guid GenerationId, DateTimeOffset ProcessedAt, long Length);
public interface IAssetDerivatives
{
    Task<AssetDerivativeStream?> OpenAsync(Guid ownerId, Guid assetId, DerivativeKind kind, CancellationToken cancellationToken);
}
