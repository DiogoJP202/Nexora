using Nexora.Domain.Content;

namespace Nexora.Application.Storage;

public enum BlobPublicationResult { Published, AlreadyExists }

public sealed record BlobObjectInfo(long Length);

public sealed record TemporaryObjectInfo(TemporaryObjectKey Key, long Length, string Sha256, string DetectedMimeType);

public interface IBlobStorage
{
    Task<BlobPublicationResult> PublishAsync(BlobStorageKey key, Stream content,
        long expectedLength, string expectedSha256, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(BlobStorageKey key, CancellationToken cancellationToken);
    Task<BlobObjectInfo?> GetInfoAsync(BlobStorageKey key, CancellationToken cancellationToken);
    Task DeleteAsync(BlobStorageKey key, CancellationToken cancellationToken);
}

public interface ITemporaryStorage
{
    Task<TemporaryObjectInfo> CreateAsync(Stream content, long maximumLength, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(TemporaryObjectKey key, CancellationToken cancellationToken);
    Task<TemporaryObjectInfo?> GetInfoAsync(TemporaryObjectKey key, CancellationToken cancellationToken);
    Task DeleteAsync(TemporaryObjectKey key, CancellationToken cancellationToken);
}

public sealed class StorageIntegrityException : IOException
{
    public StorageIntegrityException() : base("The content does not match its recorded identity.") { }
}

public sealed class StorageLimitExceededException : IOException
{
    public StorageLimitExceededException() : base("The content exceeds the allowed length.") { }
}

public sealed class UnsafeStoragePathException : IOException
{
    public UnsafeStoragePathException() : base("The storage path is not a confined private directory.") { }
}
