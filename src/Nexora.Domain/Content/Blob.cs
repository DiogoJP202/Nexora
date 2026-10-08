using Nexora.Domain.Images;

namespace Nexora.Domain.Content;

public sealed class Blob
{
    public const int Sha256Length = 64;
    public const int MaximumMimeTypeLength = 255;

    private Blob() { }

    public Blob(Guid id, string sha256, long size, string detectedMimeType, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A blob requires an identifier.", nameof(id));
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != Sha256Length || sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("The SHA-256 digest must be lowercase hexadecimal.", nameof(sha256));
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentException.ThrowIfNullOrWhiteSpace(detectedMimeType);
        if (detectedMimeType.Length > MaximumMimeTypeLength || detectedMimeType.Any(char.IsControl))
            throw new ArgumentException("The detected MIME type is invalid.", nameof(detectedMimeType));
        if (createdAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("The creation timestamp must be UTC.", nameof(createdAt));

        Id = id;
        Sha256 = sha256;
        Size = size;
        DetectedMimeType = detectedMimeType;
        StorageKey = new BlobStorageKey(id);
        State = BlobState.Staging;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public string DetectedMimeType { get; private set; } = string.Empty;
    public BlobStorageKey StorageKey { get; private set; }
    public BlobState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public ICollection<Asset> Assets { get; private set; } = [];
    public BlobImage? Image { get; private set; }

    public void MarkReady()
    {
        if (State == BlobState.Deleting)
            throw new InvalidOperationException("A deleting blob cannot become ready.");

        State = BlobState.Ready;
    }

    public void MarkDeleting() => State = BlobState.Deleting;
}
