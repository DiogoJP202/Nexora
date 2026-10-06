using Nexora.Application.Storage;
using Nexora.Domain.Content;

namespace Nexora.UnitTests;

public sealed class ContentDomainTests
{
    private static readonly DateTimeOffset UploadedAt = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BlobStatesPreserveIdentityAndDeletingIsTerminal()
    {
        var id = Guid.NewGuid();
        var blob = new Blob(id, new string('a', 64), 12, "application/octet-stream", UploadedAt);
        Assert.Equal(BlobState.Staging, blob.State);
        blob.MarkReady();
        blob.MarkReady();
        Assert.Equal(BlobState.Ready, blob.State);
        blob.MarkDeleting();
        blob.MarkDeleting();
        Assert.Equal(BlobState.Deleting, blob.State);
        Assert.Throws<InvalidOperationException>(blob.MarkReady);
        Assert.Equal(id, blob.StorageKey.BlobId);
        Assert.Equal(id, blob.Id);
    }

    [Fact]
    public void TrashAndRestoreKeepTheOriginalMetadataAndFavorite()
    {
        var asset = new Asset(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Original foto.png", UploadedAt);
        var id = asset.Id;
        var blobId = asset.BlobId;
        asset.SetFavorite(true);
        asset.MoveToTrash(UploadedAt.AddDays(1));
        asset.MoveToTrash(UploadedAt.AddDays(2));
        Assert.Equal(UploadedAt.AddDays(1), asset.DeletedAt);
        asset.Restore();
        Assert.Null(asset.DeletedAt);
        Assert.Equal(id, asset.Id);
        Assert.Equal(blobId, asset.BlobId);
        Assert.Equal("Original foto.png", asset.OriginalName);
        Assert.Equal(UploadedAt, asset.UploadedAt);
        Assert.True(asset.IsFavorite);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../secret.txt")]
    [InlineData("folder\\secret.txt")]
    [InlineData("C:secret.txt")]
    [InlineData("name\u0000.txt")]
    public void UnsafeOrEmptyOriginalNamesAreRejected(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => new Asset(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, UploadedAt));

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void BlobRequiresCanonicalSha256(string hash) =>
        Assert.ThrowsAny<ArgumentException>(() => new Blob(Guid.NewGuid(), hash, 0, "application/octet-stream", UploadedAt));

    [Fact]
    public void ContentTimestampsMustBeUtcAndDeletionCannotPrecedeUpload()
    {
        var nonUtc = UploadedAt.ToOffset(TimeSpan.FromHours(-3));
        Assert.ThrowsAny<ArgumentException>(() => new Blob(Guid.NewGuid(), new string('a', 64), 0,
            "application/octet-stream", nonUtc));
        Assert.ThrowsAny<ArgumentException>(() => new Asset(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "photo.png", nonUtc));
        var asset = new Asset(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "photo.png", UploadedAt);
        Assert.ThrowsAny<ArgumentException>(() => asset.MoveToTrash(UploadedAt.AddTicks(-1)));
    }

    [Fact]
    public void BlobStorageKeyRoundTripsWithShardsDerivedFromItsUuid()
    {
        var id = Guid.Parse("a8521274-52ae-46ef-a5cc-dcf50ffadac8");
        var key = new BlobStorageKey(id);
        Assert.Equal("blobs/a8/52/a852127452ae46efa5ccdcf50ffadac8", key.ToString());
        Assert.Equal(key, BlobStorageKey.Parse(key.ToString()));
        Assert.ThrowsAny<ArgumentException>(() => new BlobStorageKey(Guid.Empty));
        Assert.Throws<InvalidOperationException>(() => default(BlobStorageKey).ToString());
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/srv/nexora/secret")]
    [InlineData("C:\\Windows\\secret")]
    [InlineData("blobs/ff/ff/a852127452ae46efa5ccdcf50ffadac8")]
    [InlineData("blobs/A8/52/A852127452AE46EFA5CCDCF50FFADAC8")]
    [InlineData("blobs/00/00/00000000000000000000000000000000")]
    public void StorageKeyRejectsTraversalAndNoncanonicalRepresentations(string value)
    {
        Assert.False(BlobStorageKey.TryParse(value, out _));
        Assert.Throws<FormatException>(() => BlobStorageKey.Parse(value));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("A852127452AE46EFA5CCDCF50FFADAC8")]
    [InlineData("00000000000000000000000000000000")]
    public void TemporaryKeysCannotCarryArbitraryPaths(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => TemporaryObjectKey.Parse(value));
}
