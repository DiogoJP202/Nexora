using System.Net;
using System.Net.Http.Json;
using Nexora.Mobile.Core;

namespace Nexora.Mobile.Tests;

public sealed class ScopeConcurrencyTests
{
    private static ServerScope OtherScope => ServerScope.Create("https://other.example.test", "bob@example.test");

    [Fact]
    public async Task ScopeSwitchWaitsUntilPrivateCacheReadFinishes()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var asset = new AssetSnapshot(Guid.NewGuid(), "alice-private.txt", 3, "text/plain", fixture.Clock.Now, false, null);
        fixture.Handler.Handle = (_, _) => Task.FromResult(MockHandler.Json(new SyncSnapshotPage([asset], null, "alice-cursor")));
        await fixture.InitializeAsync();
        var files = new BlockingPrivateFileStore(directory.Path);
        var cache = new LocalLibraryCache(files, fixture.Client);
        await cache.SynchronizeAsync();
        files.Arm("metadata.json");

        var reading = cache.GetCachedAsync();
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = fixture.Client.ConfigureAsync(OtherScope);
        Assert.False(switching.IsCompleted);
        Assert.Equal(fixture.Scope, fixture.Client.Scope);

        files.Continue.TrySetResult();
        Assert.Equal(asset.Id, Assert.Single((await reading).Items).Id);
        await switching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty((await cache.GetCachedAsync()).Items);
    }

    [Fact]
    public async Task ScopeSwitchDuringSourceHashNeverCreatesUploadOnNewServer()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var requests = new List<Uri>();
        var uploadId = Guid.NewGuid();
        fixture.Handler.Handle = async (request, cancellationToken) =>
        {
            requests.Add(request.RequestUri!);
            var body = (await request.Content!.ReadFromJsonAsync<CreateUploadRequest>(cancellationToken))!;
            Assert.Equal("alice-private.bin", body.OriginalName);
            return MockHandler.Json(new UploadSnapshot(uploadId, body.OriginalName, body.ExpectedLength, 4, 1,
                UploadState.Finalizing, [], fixture.Clock.Now, fixture.Clock.Now, null, null, null));
        };
        await fixture.InitializeAsync();
        var files = new BlockingPrivateFileStore(directory.Path);
        var outbox = new UploadOutbox(files, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var queued = await outbox.EnqueueAsync("alice-private.bin", source);
        files.Arm(".source");

        var resuming = outbox.ResumeAsync(queued.Id);
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = fixture.Client.ConfigureAsync(OtherScope);
        Assert.False(switching.IsCompleted);
        Assert.Empty(requests);

        files.Continue.TrySetResult();
        Assert.Equal(OutboxState.Finalizing, (await resuming).State);
        await switching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fixture.Scope.Server.Host, Assert.Single(requests).Host);
        Assert.Empty(await outbox.ListAsync());
    }

    [Fact]
    public async Task ScopeSwitchDuringChunkHashNeverSendsBytesOnNewServer()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var requests = new List<Uri>();
        var uploadId = Guid.NewGuid();
        var confirmed = false;
        UploadSnapshot Snapshot(UploadState state = UploadState.Open) => new(uploadId, "alice-private.bin", 3, 4, 1,
            state, confirmed ? [0] : [], fixture.Clock.Now, fixture.Clock.Now, null, null, null);
        fixture.Handler.Handle = async (request, cancellationToken) =>
        {
            requests.Add(request.RequestUri!);
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal([1, 2, 3], await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                confirmed = true;
                return MockHandler.Json(new UploadChunkReceipt(0, 3,
                    request.Headers.GetValues("X-Chunk-SHA256").Single(), false));
            }
            return MockHandler.Json(Snapshot(request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
                ? UploadState.Finalizing : UploadState.Open));
        };
        await fixture.InitializeAsync();
        var files = new BlockingPrivateFileStore(directory.Path);
        var outbox = new UploadOutbox(files, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var queued = await outbox.EnqueueAsync("alice-private.bin", source);
        // The first two source streams validate the file; the third hashes a chunk
        // after the outbox has already checked its captured scope.
        files.Arm(".source", occurrence: 3);

        var resuming = outbox.ResumeAsync(queued.Id);
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = fixture.Client.ConfigureAsync(OtherScope);
        Assert.False(switching.IsCompleted);
        Assert.False(confirmed);

        files.Continue.TrySetResult();
        Assert.Equal(OutboxState.Finalizing, (await resuming).State);
        await switching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(confirmed);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.Equal(fixture.Scope.Server.Host, request.Host));
    }

    [Fact]
    public async Task ScopeSwitchWaitsForNestedRequestEvenAfterEnclosingCallbackReturns()
    {
        var fixture = new ClientFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Handle = async (request, cancellationToken) =>
        {
            entered.TrySetResult();
            await continuation.Task.WaitAsync(cancellationToken);
            Assert.Equal(fixture.Scope.Server.Host, request.RequestUri!.Host);
            return MockHandler.Json(new AssetPage([], null));
        };
        await fixture.InitializeAsync();
        Task<AssetPage>? request = null;
        await fixture.Client.ExecuteInScopeAsync(() =>
        {
            request = fixture.Client.ListAssetsAsync();
            return Task.CompletedTask;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = fixture.Client.ConfigureAsync(OtherScope);
        Assert.False(switching.IsCompleted);
        continuation.TrySetResult();
        await request!.WaitAsync(TimeSpan.FromSeconds(5));
        await switching.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<LoginRequiredException>(() => fixture.Client.ListAssetsAsync());
    }

    [Fact]
    public async Task CancellingOperationReleasesScopeForPendingSwitch()
    {
        var fixture = new ClientFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Handle = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return MockHandler.Json(new AssetPage([], null), HttpStatusCode.OK);
        };
        await fixture.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var request = fixture.Client.ListAssetsAsync(cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = fixture.Client.ConfigureAsync(OtherScope);
        Assert.False(switching.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await switching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OtherScope, fixture.Client.Scope);
    }
}

internal sealed class BlockingPrivateFileStore(string root) : IPrivateFileStore
{
    private readonly AppPrivateFileStore inner = new(root);
    private string? suffix;
    private int occurrence;
    private int matches;
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Arm(string pathSuffix, int occurrence = 1)
    {
        suffix = pathSuffix;
        this.occurrence = occurrence;
    }
    public string PathOf(string path) => inner.PathOf(path);
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = await inner.OpenReadAsync(path, cancellationToken);
        return suffix is not null && path.EndsWith(suffix, StringComparison.Ordinal) && ++matches == occurrence
            ? new BlockingReadStream(stream, Entered, Continue) : stream;
    }
    public Task<Stream> CreateAsync(string path, CancellationToken cancellationToken = default) => inner.CreateAsync(path, cancellationToken);
    public bool Exists(string path) => inner.Exists(path);
    public long Length(string path) => inner.Length(path);
    public void Move(string source, string destination) => inner.Move(source, destination);
    public void Delete(string path) => inner.Delete(path);
    public IEnumerable<string> List(string directory, string pattern) => inner.List(directory, pattern);
}

internal sealed class BlockingReadStream(Stream inner, TaskCompletionSource entered, TaskCompletionSource continuation) : Stream
{
    private bool paused;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!paused)
        {
            paused = true;
            entered.TrySetResult();
            await continuation.Task.WaitAsync(cancellationToken);
        }
        return await inner.ReadAsync(buffer, cancellationToken);
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
