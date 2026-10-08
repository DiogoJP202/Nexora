using System.Buffers.Binary;

namespace Nexora.Application.Content;

public sealed record AssetPage(IReadOnlyList<AssetSnapshot> Items, string? NextCursor);

// The caller transfers ownership of Content to the HTTP stream result or disposes it.
public sealed record AssetContentStream(AssetSnapshot Asset, Stream Content, Guid BlobId);

public interface IAssetLibrary
{
    Task<AssetPage> ListAsync(Guid ownerId, int limit, string? cursor, CancellationToken cancellationToken);
    Task<AssetPage> ListAsync(Guid ownerId, int limit, string? cursor, AssetListQuery query, CancellationToken cancellationToken);
    Task<AssetPage> ListTrashAsync(Guid ownerId, int limit, string? cursor, CancellationToken cancellationToken);
    Task<AssetSnapshot?> FindAsync(Guid ownerId, Guid assetId, CancellationToken cancellationToken);
    Task<AssetContentStream?> OpenContentAsync(Guid ownerId, Guid assetId, CancellationToken cancellationToken);
}

public sealed class AssetContentUnavailableException : IOException
{
    public AssetContentUnavailableException() : base("The original content is not available.") { }
}

public sealed record AssetPageAnchor(DateTimeOffset UploadedAt, Guid AssetId);

public static class AssetCursor
{
    private const int PayloadSize = 41;
    private const int EncodedSize = 55;

    public static string Encode(Guid ownerId, DateTimeOffset uploadedAt, Guid assetId)
    {
        if (ownerId == Guid.Empty || assetId == Guid.Empty || uploadedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("The cursor requires an owner, an asset and a UTC timestamp.");

        Span<byte> payload = stackalloc byte[PayloadSize];
        payload[0] = 1;
        ownerId.TryWriteBytes(payload[1..17]);
        BinaryPrimitives.WriteInt64BigEndian(payload[17..25], uploadedAt.UtcTicks);
        assetId.TryWriteBytes(payload[25..]);
        return ToBase64Url(payload);
    }

    public static AssetPageAnchor Decode(string value, Guid ownerId)
    {
        if (ownerId == Guid.Empty || value is null || value.Length != EncodedSize ||
            value.Any(character => character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9') and not '-' and not '_'))
            throw InvalidCursor();

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }

        if (payload.Length != PayloadSize || payload[0] != 1 || ToBase64Url(payload) != value ||
            new Guid(payload.AsSpan(1, 16)) != ownerId)
            throw InvalidCursor();
        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(17, 8));
        var assetId = new Guid(payload.AsSpan(25, 16));
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || assetId == Guid.Empty)
            throw InvalidCursor();
        return new AssetPageAnchor(new DateTimeOffset(ticks, TimeSpan.Zero), assetId);
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static FormatException InvalidCursor() => new("The asset cursor is invalid.");
}
