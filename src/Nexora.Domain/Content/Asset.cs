namespace Nexora.Domain.Content;

public sealed class Asset
{
    public const int MaximumOriginalNameLength = 255;

    private Asset() { }

    public Asset(Guid id, Guid ownerId, Guid blobId, string originalName, DateTimeOffset uploadedAt)
    {
        if (id == Guid.Empty || ownerId == Guid.Empty || blobId == Guid.Empty)
            throw new ArgumentException("An asset requires valid identifiers.");
        ValidateOriginalName(originalName);
        RequireUtc(uploadedAt);

        Id = id;
        OwnerId = ownerId;
        BlobId = blobId;
        OriginalName = originalName;
        UploadedAt = uploadedAt;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid BlobId { get; private set; }
    public string OriginalName { get; private set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; private set; }
    public bool IsFavorite { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Blob Blob { get; private set; } = null!;

    public void MoveToTrash(DateTimeOffset now)
    {
        RequireUtc(now);
        if (now < UploadedAt)
            throw new ArgumentException("The deletion timestamp precedes the upload.", nameof(now));

        DeletedAt ??= now;
    }

    public void Restore() => DeletedAt = null;

    public void SetFavorite(bool isFavorite) => IsFavorite = isFavorite;

    public void Rename(string originalName)
    {
        ValidateOriginalName(originalName);
        OriginalName = originalName;
    }

    public static void ValidateOriginalName(string originalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalName);
        if (originalName is "." or ".." || originalName.Length > MaximumOriginalNameLength
            || originalName.Any(character => char.IsControl(character) || character is '/' or '\\' or ':'))
            throw new ArgumentException("The original filename is invalid.", nameof(originalName));
    }

    private static void RequireUtc(DateTimeOffset timestamp)
    {
        if (timestamp.Offset != TimeSpan.Zero)
            throw new ArgumentException("The timestamp must be UTC.", nameof(timestamp));
    }
}
