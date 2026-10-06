namespace Nexora.Domain.Content;

public readonly record struct BlobStorageKey
{
    public BlobStorageKey(Guid blobId)
    {
        if (blobId == Guid.Empty)
            throw new ArgumentException("A storage key requires a blob identifier.", nameof(blobId));

        BlobId = blobId;
    }

    public Guid BlobId { get; }

    public override string ToString()
    {
        if (BlobId == Guid.Empty)
            throw new InvalidOperationException("The storage key is not initialized.");

        var identifier = BlobId.ToString("N");
        return $"blobs/{identifier[..2]}/{identifier[2..4]}/{identifier}";
    }

    public static BlobStorageKey Parse(string value)
    {
        if (!TryParse(value, out var key))
            throw new FormatException("The storage key is invalid.");

        return key;
    }

    public static bool TryParse(string? value, out BlobStorageKey key)
    {
        key = default;
        if (value is null || value.Length != 44 || !value.StartsWith("blobs/", StringComparison.Ordinal)
            || value[8] != '/' || value[11] != '/'
            || !Guid.TryParseExact(value.AsSpan(12), "N", out var identifier) || identifier == Guid.Empty)
            return false;

        var parsed = new BlobStorageKey(identifier);
        if (!string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
            return false;

        key = parsed;
        return true;
    }
}
