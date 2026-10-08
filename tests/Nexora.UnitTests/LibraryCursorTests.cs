using System.Buffers.Binary;
using Nexora.Application.Content;
using Nexora.Domain.Content;

namespace Nexora.UnitTests;

public sealed class LibraryCursorTests
{
    [Fact]
    public void Cursor_roundtrips_and_is_bound_to_owner_scope_filters_and_sort()
    {
        var owner = Guid.NewGuid();
        var query = new AssetListQuery(true, true, AssetSort.Timeline);
        var watermark = DateTimeOffset.UtcNow;
        var anchor = new LibraryPageAnchor(watermark, watermark.AddYears(-1), Guid.NewGuid());
        var value = LibraryCursor.Encode(owner, AssetListScope.Library, query, anchor);
        Assert.Equal(anchor, LibraryCursor.Decode(value, owner, AssetListScope.Library, query));
        Assert.Throws<FormatException>(() => LibraryCursor.Decode(value, Guid.NewGuid(), AssetListScope.Library, query));
        Assert.Throws<FormatException>(() => LibraryCursor.Decode(value, owner, AssetListScope.Library, query with { IsFavorite = false }));
        Assert.Throws<FormatException>(() => LibraryCursor.Decode(value, owner, AssetListScope.Library, query with { IsFavorite = null }));
        Assert.Throws<FormatException>(() => LibraryCursor.Decode(value, owner, AssetListScope.Library, query with { Sort = AssetSort.UploadedAt }));
        Assert.Throws<FormatException>(() => LibraryCursor.Decode(value, owner, AssetListScope.Trash, new AssetListQuery()));
        var uploaded = LibraryCursor.Encode(owner, AssetListScope.Library, new AssetListQuery(), anchor);
        Assert.Throws<FormatException>(() => LibraryCursor.Decode(uploaded, owner, AssetListScope.Library, new AssetListQuery(true)));
    }

    [Fact]
    public void Cursor_rejects_noncanonical_encoding_unknown_version_and_invalid_payload_fields()
    {
        var owner = Guid.NewGuid();
        var query = new AssetListQuery();
        var now = DateTimeOffset.UtcNow;
        var value = LibraryCursor.Encode(owner, AssetListScope.Library, query, new LibraryPageAnchor(now, now, Guid.NewGuid()));
        foreach (var invalid in new[] { value + "=", value[..^1], value[..^1] + "+", "" })
            Assert.Throws<FormatException>(() => LibraryCursor.Decode(invalid, owner, AssetListScope.Library, query));
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var noncanonical = value[..^1] + alphabet[alphabet.IndexOf(value[^1], StringComparison.Ordinal) + 1];
        Assert.Throws<FormatException>(() => LibraryCursor.Decode(noncanonical, owner, AssetListScope.Library, query));
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "==");
        void Reject(Action<byte[]> alter)
        {
            var changed = bytes.ToArray();
            alter(changed);
            Assert.Throws<FormatException>(() => LibraryCursor.Decode(Encode(changed), owner, AssetListScope.Library, query));
        }
        Reject(payload => payload[0] = 9);
        Reject(payload => payload[19] = 8);
        Reject(payload => BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(20, 8), -1));
        Reject(payload => BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(28, 8), long.MaxValue));
        Reject(payload => payload.AsSpan(36, 16).Clear());
    }

    [Fact]
    public void Timeline_requires_images_and_cursors_require_utc_valid_identifiers_and_scope()
    {
        Assert.Throws<ArgumentException>(() => new AssetListQuery(Sort: AssetSort.Timeline).Validate());
        Assert.Throws<ArgumentException>(() => new AssetListQuery(Sort: (AssetSort)3).Validate());
        var now = DateTimeOffset.UtcNow;
        var query = new AssetListQuery();
        var anchor = new LibraryPageAnchor(now, now, Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => LibraryCursor.Encode(Guid.Empty, AssetListScope.Library, query, anchor));
        Assert.Throws<ArgumentException>(() => LibraryCursor.Encode(Guid.NewGuid(), AssetListScope.Library, query, anchor with { AssetId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => LibraryCursor.Encode(Guid.NewGuid(), AssetListScope.Library, query,
            anchor with { Watermark = now.ToOffset(TimeSpan.FromHours(3)) }));
        Assert.Throws<ArgumentException>(() => LibraryCursor.Encode(Guid.NewGuid(), AssetListScope.Library, query,
            anchor with { OrderedAt = now.ToOffset(TimeSpan.FromHours(3)) }));
        Assert.Throws<ArgumentException>(() => LibraryCursor.Encode(Guid.NewGuid(), (AssetListScope)9, query, anchor));
        Assert.Throws<ArgumentException>(() => LibraryCursor.Encode(Guid.NewGuid(), AssetListScope.Trash, new AssetListQuery(true), anchor));
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("unsafe:name.png")]
    [InlineData(" ")]
    public void Rename_uses_the_same_filename_rules_as_import(string name)
    {
        var asset = new Asset(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "original.png", DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => asset.Rename(name));
        Assert.Equal("original.png", asset.OriginalName);
        asset.Rename("renamed.png");
        Assert.Equal("renamed.png", asset.OriginalName);
        Assert.Throws<ArgumentException>(() => new AssetUpdate().Validate());
        new AssetUpdate(IsFavorite: false).Validate();
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
