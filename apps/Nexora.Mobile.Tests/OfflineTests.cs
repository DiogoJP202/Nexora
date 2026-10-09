using System.Net;
using Nexora.Mobile.Core;

namespace Nexora.Mobile.Tests;

public sealed class OfflineTests
{
    [Fact]
    public async Task ServerAndAccountSwitchNeverExposesAnotherOwnersCacheOrOutbox()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var asset = Asset("private name");
        fixture.Handler.Handle = (_, _) => Task.FromResult(MockHandler.Json(new SyncSnapshotPage([asset], null, "owner-a-cursor")));
        await fixture.InitializeAsync();
        var cache = new LocalLibraryCache(directory.Path, fixture.Client);
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        await cache.SynchronizeAsync();
        using var content = new MemoryStream([1, 2, 3]);
        await outbox.EnqueueAsync("private.bin", content);
        foreach (var scope in new[]
        {
            ServerScope.Create("https://other.example.test", fixture.Scope.Login),
            ServerScope.Create(fixture.Scope.Server.AbsoluteUri, "bob@example.test")
        })
        {
            await fixture.Client.ConfigureAsync(scope);
            Assert.Empty((await cache.GetCachedAsync()).Items);
            Assert.Empty(await outbox.ListAsync());
            Assert.False(fixture.Client.IsSignedIn);
        }
        await fixture.Client.ConfigureAsync(fixture.Scope);
        Assert.Single((await cache.GetCachedAsync()).Items);
        Assert.Single(await outbox.ListAsync());
        var metadata = await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "library", fixture.Scope.Key, "metadata.json"));
        Assert.DoesNotContain("access-secret", metadata);
        Assert.DoesNotContain("refresh-secret", metadata);
    }

    [Fact]
    public async Task FullSnapshotOnlyReplacesCacheAfterEveryPageCompletes()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var previous = Asset("previous");
        var replacement = Asset("replacement");
        var refreshing = false;
        fixture.Handler.Handle = (request, _) =>
        {
            var uri = request.RequestUri!;
            if (!refreshing) return Task.FromResult(MockHandler.Json(new SyncSnapshotPage([previous], null, "old-delta")));
            if (uri.AbsolutePath == "/api/sync/changes")
                return Task.FromResult(MockHandler.Json(new { code = "sync_reset_required" }, HttpStatusCode.Conflict));
            if (uri.Query.Contains("page2", StringComparison.Ordinal)) throw new HttpRequestException("Offline halfway through snapshot");
            return Task.FromResult(MockHandler.Json(new SyncSnapshotPage([replacement], "page2", "new-delta")));
        };
        await fixture.InitializeAsync();
        var cache = new LocalLibraryCache(directory.Path, fixture.Client);
        await cache.SynchronizeAsync();
        refreshing = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => cache.SynchronizeAsync());
        var saved = await cache.GetCachedAsync();
        Assert.Equal("old-delta", saved.Cursor);
        Assert.Equal(previous.Id, Assert.Single(saved.Items).Id);
    }

    [Fact]
    public async Task SnapshotAcceptsFreshlyProtectedOpaqueCursorOnEachPage()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var first = Asset("first");
        var second = Asset("second");
        fixture.Handler.Handle = (request, _) => Task.FromResult(request.RequestUri!.Query.Contains("page2", StringComparison.Ordinal)
            ? MockHandler.Json(new SyncSnapshotPage([second], null, "same-watermark-new-encryption"))
            : MockHandler.Json(new SyncSnapshotPage([first], "page2", "same-watermark-first-encryption")));
        await fixture.InitializeAsync();
        var cache = new LocalLibraryCache(directory.Path, fixture.Client);
        var result = await cache.SynchronizeAsync();
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("same-watermark-new-encryption", result.Cursor);
    }

    [Fact]
    public async Task IncrementalUpsertsTrashAndPurgesApplyWithCursorInOneAtomicSave()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var kept = Asset("kept");
        var purged = Asset("purged");
        var first = true;
        fixture.Handler.Handle = (_, _) =>
        {
            if (first)
            {
                first = false;
                return Task.FromResult(MockHandler.Json(new SyncSnapshotPage([kept, purged], null, "first")));
            }
            return Task.FromResult(MockHandler.Json(new SyncChangePage([
                new SyncChange(1, "upsert", kept.Id, kept with { IsFavorite = true, DeletedAt = DateTimeOffset.UtcNow }),
                new SyncChange(2, "purge", purged.Id, null)], "second", false)));
        };
        await fixture.InitializeAsync();
        var cache = new LocalLibraryCache(directory.Path, fixture.Client);
        await cache.SynchronizeAsync();
        var result = await cache.SynchronizeAsync();
        Assert.Equal("second", result.Cursor);
        Assert.True(Assert.Single(result.Items).IsFavorite);
        Assert.NotNull(result.Items[0].DeletedAt);
        var restartedCache = new LocalLibraryCache(directory.Path, fixture.Client);
        Assert.Equal("second", (await restartedCache.GetCachedAsync()).Cursor);
        Assert.Single((await restartedCache.GetCachedAsync()).Items);
    }

    [Fact]
    public void PrivateFileStoreRejectsTraversalAndAbsolutePaths()
    {
        using var directory = new PrivateDirectory();
        var store = new AppPrivateFileStore(directory.Path);
        Assert.Throws<ArgumentException>(() => store.PathOf("../outside"));
        Assert.Throws<ArgumentException>(() => store.PathOf(System.IO.Path.GetTempPath()));
    }

    private static AssetSnapshot Asset(string name) => new(Guid.NewGuid(), name, 5, "text/plain", DateTimeOffset.UtcNow, false, null);
}
