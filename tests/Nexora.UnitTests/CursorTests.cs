using System.Buffers.Binary;
using Nexora.Application.Content;

namespace Nexora.UnitTests;

public sealed class CursorTests
{
    [Fact]
    public void CanonicalCursorKeepsTheUtcTimestampAndAssetAndIsBoundToItsOwner()
    {
        var owner = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero).AddTicks(12340);
        var encoded = AssetCursor.Encode(owner, timestamp, asset);

        Assert.Equal(new AssetPageAnchor(timestamp, asset), AssetCursor.Decode(encoded, owner));
        Assert.DoesNotContain('=', encoded);
        Assert.Throws<FormatException>(() => AssetCursor.Decode(encoded, Guid.NewGuid()));
        Assert.ThrowsAny<ArgumentException>(() => AssetCursor.Encode(owner, timestamp.ToOffset(TimeSpan.FromHours(-3)), asset));
    }

    [Fact]
    public void CursorRejectsPaddingUnsupportedVersionsInvalidTimesAndEmptyIds()
    {
        var owner = Guid.NewGuid();
        var cursor = AssetCursor.Encode(owner, DateTimeOffset.UtcNow, Guid.NewGuid());
        Assert.Throws<FormatException>(() => AssetCursor.Decode(cursor + "=", owner));
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var noncanonical = cursor[..^1] + alphabet[alphabet.IndexOf(cursor[^1], StringComparison.Ordinal) + 1];
        Assert.Throws<FormatException>(() => AssetCursor.Decode(noncanonical, owner));
        var payload = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + "=");
        payload[0] = 2;
        Assert.Throws<FormatException>(() => AssetCursor.Decode(Encode(payload), owner));
        payload[0] = 1;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(17, 8), -1);
        Assert.Throws<FormatException>(() => AssetCursor.Decode(Encode(payload), owner));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(17, 8), DateTimeOffset.UtcNow.UtcTicks);
        payload.AsSpan(25, 16).Clear();
        Assert.Throws<FormatException>(() => AssetCursor.Decode(Encode(payload), owner));
    }

    private static string Encode(byte[] payload) => Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
