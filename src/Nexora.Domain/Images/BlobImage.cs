using Nexora.Domain.Content;
using Nexora.Domain.Uploads;

namespace Nexora.Domain.Images;

public sealed class BlobImage
{
    private BlobImage() { }

    public BlobImage(Guid blobId, DateTimeOffset createdAt)
    {
        if (blobId == Guid.Empty || createdAt.Offset != TimeSpan.Zero) throw new ArgumentException("Invalid image identity or timestamp.");
        BlobId = blobId;
        CreatedAt = createdAt;
    }

    public Guid BlobId { get; private set; }
    public ImageProcessingState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public int? Width { get; private set; }
    public int? Height { get; private set; }
    public DateTime? CapturedAtLocal { get; private set; }
    public DateTimeOffset? CapturedAtUtc { get; private set; }
    public Guid? DerivativeGenerationId { get; private set; }
    public long? ThumbnailLength { get; private set; }
    public string? ThumbnailSha256 { get; private set; }
    public long? PreviewLength { get; private set; }
    public string? PreviewSha256 { get; private set; }
    public long ReservedBytes { get; private set; }
    public string? FailureCode { get; private set; }
    public Blob Blob { get; private set; } = null!;

    public static bool Supports(string mimeType) => mimeType is "image/jpeg" or "image/png" or "image/webp";

    public void BeginProcessing(long reservation)
    {
        if (State is ImageProcessingState.Ready or ImageProcessingState.Failed || reservation <= 0)
            throw new InvalidOperationException("The image cannot be processed.");
        if (ReservedBytes > reservation) throw new InvalidOperationException("An active reservation cannot shrink.");
        ReservedBytes = reservation;
        State = ImageProcessingState.Processing;
        FailureCode = null;
    }

    public void Complete(Guid generationId, int width, int height, DateTime? capturedLocal, DateTimeOffset? capturedUtc,
        long thumbnailLength, string thumbnailSha256, long previewLength, string previewSha256, DateTimeOffset processedAt)
    {
        if (State != ImageProcessingState.Processing || generationId == Guid.Empty || width <= 0 || height <= 0
            || thumbnailLength <= 0 || previewLength <= 0 || processedAt.Offset != TimeSpan.Zero
            || (capturedLocal is { Kind: not DateTimeKind.Unspecified }) || (capturedUtc is { Offset: var offset } && offset != TimeSpan.Zero))
            throw new ArgumentException("The completed image metadata is invalid.");
        ValidateDigest(thumbnailSha256);
        ValidateDigest(previewSha256);
        if (thumbnailLength > ReservedBytes || previewLength > ReservedBytes - thumbnailLength)
            throw new InvalidOperationException("The derivatives exceed their reservation.");
        Width = width;
        Height = height;
        CapturedAtLocal = capturedLocal;
        CapturedAtUtc = capturedUtc;
        DerivativeGenerationId = generationId;
        ThumbnailLength = thumbnailLength;
        ThumbnailSha256 = thumbnailSha256;
        PreviewLength = previewLength;
        PreviewSha256 = previewSha256;
        ProcessedAt = processedAt;
        FailureCode = null;
        State = ImageProcessingState.Ready;
    }

    public void Fail(string code, bool retryable)
    {
        if (State is ImageProcessingState.Ready or ImageProcessingState.Failed)
            throw new InvalidOperationException("A terminal image cannot fail again.");
        UploadSession.ValidateFailureCode(code);
        State = retryable ? ImageProcessingState.Pending : ImageProcessingState.Failed;
        FailureCode = code;
    }

    public void FinishCleanup()
    {
        if (State is not (ImageProcessingState.Ready or ImageProcessingState.Failed))
            throw new InvalidOperationException("An active image still needs its reservation.");
        ReservedBytes = 0;
    }

    private static void ValidateDigest(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != Blob.Sha256Length || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("The SHA-256 digest must be lowercase hexadecimal.");
    }
}
