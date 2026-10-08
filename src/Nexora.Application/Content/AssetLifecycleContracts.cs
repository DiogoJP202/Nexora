using System.Buffers.Binary;
using Nexora.Domain.Content;

namespace Nexora.Application.Content;

public enum AssetSort { UploadedAt, Timeline }
public enum AssetListScope { Library, Trash }

public sealed record AssetListQuery(bool ImagesOnly = false, bool? IsFavorite = null, AssetSort Sort = AssetSort.UploadedAt)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Sort) || (Sort == AssetSort.Timeline && !ImagesOnly))
            throw new ArgumentException("Timeline ordering requires the image filter.");
    }
}

public sealed record AssetUpdate(string? OriginalName = null, bool? IsFavorite = null)
{
    public void Validate()
    {
        if (OriginalName is null && IsFavorite is null) throw new ArgumentException("An update requires a value.");
        if (OriginalName is not null) Asset.ValidateOriginalName(OriginalName);
    }
}

public interface IAssetLifecycle
{
    Task<AssetSnapshot?> UpdateAsync(Guid ownerId, Guid assetId, AssetUpdate update, CancellationToken cancellationToken);
    Task<bool> MoveToTrashAsync(Guid ownerId, Guid assetId, CancellationToken cancellationToken);
    Task<AssetSnapshot?> RestoreAsync(Guid ownerId, Guid assetId, CancellationToken cancellationToken);
}

public sealed record LibraryPageAnchor(DateTimeOffset Watermark, DateTimeOffset OrderedAt, Guid AssetId);

// The cursor describes a page boundary, rather than granting access to any resource.
// Every consuming query independently checks its authenticated owner.
public static class LibraryCursor
{
    private const int PayloadSize = 52;
    private const int EncodedSize = 70;

    public static string Encode(Guid ownerId, AssetListScope scope, AssetListQuery query, LibraryPageAnchor anchor)
    {
        ValidateContext(ownerId, scope, query);
        if (anchor.AssetId == Guid.Empty || anchor.Watermark.Offset != TimeSpan.Zero || anchor.OrderedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A cursor requires an asset and UTC timestamps.");
        Span<byte> payload = stackalloc byte[PayloadSize];
        payload[0] = 2;
        ownerId.TryWriteBytes(payload[1..17]);
        payload[17] = (byte)scope;
        payload[18] = (byte)query.Sort;
        payload[19] = Flags(query);
        BinaryPrimitives.WriteInt64BigEndian(payload[20..28], anchor.Watermark.UtcTicks);
        BinaryPrimitives.WriteInt64BigEndian(payload[28..36], anchor.OrderedAt.UtcTicks);
        anchor.AssetId.TryWriteBytes(payload[36..52]);
        return EncodePayload(payload);
    }

    public static LibraryPageAnchor Decode(string value, Guid ownerId, AssetListScope scope, AssetListQuery query)
    {
        ValidateContext(ownerId, scope, query);
        if (value is null || value.Length != EncodedSize || value.Any(character =>
            character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_'))
            throw Invalid();
        byte[] payload;
        try { payload = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=="); }
        catch (FormatException) { throw Invalid(); }
        if (payload.Length != PayloadSize || payload[0] != 2 || EncodePayload(payload) != value ||
            new Guid(payload.AsSpan(1, 16)) != ownerId || payload[17] != (byte)scope ||
            payload[18] != (byte)query.Sort || payload[19] != Flags(query)) throw Invalid();
        var watermark = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(20, 8));
        var orderedAt = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(28, 8));
        var id = new Guid(payload.AsSpan(36, 16));
        if (watermark < 0 || watermark > DateTimeOffset.MaxValue.UtcTicks || orderedAt < 0 ||
            orderedAt > DateTimeOffset.MaxValue.UtcTicks || id == Guid.Empty) throw Invalid();
        return new LibraryPageAnchor(new DateTimeOffset(watermark, TimeSpan.Zero), new DateTimeOffset(orderedAt, TimeSpan.Zero), id);
    }

    private static byte Flags(AssetListQuery query) => (byte)((query.ImagesOnly ? 1 : 0) |
        (query.IsFavorite.HasValue ? 2 : 0) | (query.IsFavorite == true ? 4 : 0));

    private static void ValidateContext(Guid ownerId, AssetListScope scope, AssetListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        if (ownerId == Guid.Empty || !Enum.IsDefined(scope) || (scope == AssetListScope.Trash && query != new AssetListQuery()))
            throw new ArgumentException("The cursor context is invalid.");
    }

    private static string EncodePayload(ReadOnlySpan<byte> payload) =>
        Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static FormatException Invalid() => new("The library cursor is invalid.");
}
