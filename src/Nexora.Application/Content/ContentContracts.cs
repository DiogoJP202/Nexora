using Nexora.Domain.Content;

namespace Nexora.Application.Content;

public sealed record AssetImportRequest(Guid OwnerId, string OriginalName, long ExpectedLength, string? ExpectedSha256 = null);

public sealed record BlobDescriptor(Guid Id, string Sha256, long Size, string DetectedMimeType,
    BlobStorageKey StorageKey, BlobState State);

public sealed record AssetSnapshot(Guid Id, string OriginalName, long Size, string DetectedMimeType,
    DateTimeOffset UploadedAt, bool IsFavorite, DateTimeOffset? DeletedAt);

public enum AssetImportStatus { Created, Reused, AssetInTrash, BlobUnavailable }

public sealed record AssetImportResult(AssetImportStatus Status, AssetSnapshot? Asset);

public interface IAssetIngestionService
{
    Task<AssetImportResult> ImportAsync(AssetImportRequest request, Stream content, CancellationToken cancellationToken);
}

// Specific catalog operations keep database transactions out of the streaming coordinator.
public interface IContentCatalog
{
    Task<BlobDescriptor> GetOrCreateBlobAsync(Guid ownerId, string sha256, long size,
        string detectedMimeType, DateTimeOffset createdAt, CancellationToken cancellationToken);
    Task<AssetImportResult> CompleteAsync(Guid ownerId, Guid blobId, string originalName,
        DateTimeOffset uploadedAt, CancellationToken cancellationToken);
}
