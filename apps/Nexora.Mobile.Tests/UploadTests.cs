using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Nexora.Mobile.Core;

namespace Nexora.Mobile.Tests;

public sealed class UploadTests
{
    [Fact]
    public async Task LostChunkAcknowledgementIsResolvedByServerSnapshotAfterAppRestart()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new UploadServer(fixture);
        server.LoseFirstChunk = true;
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3, 4, 5, 6, 7]);
        var item = await outbox.EnqueueAsync("example.bin", source);
        source.Dispose(); // The picker/source need not remain available after enqueue.
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(item.Id));
        Assert.Equal([0], server.Chunks.ToArray());
        var restartedClient = fixture.NewClient();
        await restartedClient.ConfigureAsync(fixture.Scope);
        var restarted = new UploadOutbox(directory.Path, restartedClient);
        var result = await restarted.ResumeAsync(item.Id);
        Assert.Equal(OutboxState.Completed, result.State);
        Assert.NotNull(result.Result);
        Assert.Equal(1, server.Creates);
        Assert.Equal([0, 1], server.PutNumbers);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
    }

    [Fact]
    public async Task LostCreateAcknowledgementReusesPersistedRequestIdWithoutDuplicateSession()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new UploadServer(fixture) { LoseFirstCreate = true };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("example.bin", source);
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(item.Id));
        Assert.Equal(OutboxState.CreateInFlight, Assert.Single(await outbox.ListAsync()).State);
        var restarted = new UploadOutbox(directory.Path, fixture.Client);
        var result = await restarted.ResumeAsync(item.Id);
        Assert.Equal(OutboxState.Completed, result.State);
        Assert.Equal(1, server.Creates);
        Assert.Equal([item.Id, item.Id], server.CreateKeys);
    }

    [Fact]
    public async Task ChangedPrivateSourceIsRejectedBeforeSendingAnyRequest()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var calls = 0;
        fixture.Handler.Handle = (_, _) => { calls++; throw new InvalidOperationException("No call expected"); };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("example.bin", source);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, item.SourcePath), [9, 9, 9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => outbox.ResumeAsync(item.Id));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EnqueueEnforcesFileSizeWhileCopyingAndRemovesPartialPrivateSource()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client, maximumFileBytes: 2);
        using var source = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => outbox.EnqueueAsync("example.bin", source));
        Assert.Empty(await outbox.ListAsync());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.source", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RestoredServerMissingKnownUploadReusesRequestIdAndPersistsResetAcrossLostCreateResponse()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new UploadServer(fixture) { LoseFirstChunk = true };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3, 4, 5, 6, 7]);
        var item = await outbox.EnqueueAsync("example.bin", source);
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(item.Id));
        var oldServerId = Assert.Single(await outbox.ListAsync()).ServerUploadId;
        Assert.NotNull(oldServerId);

        server.RestoreBeforeUpload();
        server.LoseFirstCreate = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(item.Id));
        var reset = Assert.Single(await outbox.ListAsync());
        Assert.Null(reset.ServerUploadId);
        Assert.Equal(OutboxState.CreateInFlight, reset.State);
        Assert.Empty(reset.ConfirmedChunks);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));

        var restartedClient = fixture.NewClient();
        await restartedClient.ConfigureAsync(fixture.Scope);
        var restarted = new UploadOutbox(directory.Path, restartedClient);
        var result = await restarted.ResumeAsync(item.Id);
        Assert.Equal(OutboxState.Completed, result.State);
        Assert.NotEqual(oldServerId, result.ServerUploadId);
        Assert.Equal(2, server.Creates); // One before restore and one in the recovered database.
        Assert.Equal([item.Id, item.Id, item.Id], server.CreateKeys);
        Assert.Equal([0, 0, 1], server.PutNumbers);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
    }

    [Fact]
    public async Task MissingUploadAfterRestoreDoesNotCreateAgainWithChangedPrivateSource()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new UploadServer(fixture) { LoseFirstChunk = true };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("example.bin", source);
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(item.Id));
        server.RestoreBeforeUpload();
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, item.SourcePath), [9, 9, 9]);

        await Assert.ThrowsAsync<InvalidDataException>(() => outbox.ResumeAsync(item.Id));
        Assert.Equal(1, server.Creates);
        var reset = Assert.Single(await outbox.ListAsync());
        Assert.Null(reset.ServerUploadId);
        Assert.Equal(OutboxState.Pending, reset.State);
        Assert.Empty(reset.ConfirmedChunks);
    }

    [Fact]
    public async Task RemoveAcceptsMissingRemoteUploadAfterRestoreAndDeletesLocalCopy()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new UploadServer(fixture) { LoseFirstChunk = true };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("example.bin", source);
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(item.Id));
        server.RestoreBeforeUpload();

        await outbox.RemoveAsync(item.Id);
        Assert.Empty(await outbox.ListAsync());
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
        Assert.Equal(1, server.Cancels);
        Assert.Equal(1, server.Creates);
    }

    [Fact]
    public async Task RemovePreservesLocalItemWhenRemoteCancellationConflicts()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new UploadServer(fixture) { LoseFirstChunk = true, CancellationConflicts = true };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("example.bin", source);
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(item.Id));

        var failure = await Assert.ThrowsAsync<NexoraApiException>(() => outbox.RemoveAsync(item.Id));
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        Assert.Single(await outbox.ListAsync());
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
    }

    [Fact]
    public async Task RemoveRecoversLostCreationAndPersistsRemoteIdBeforeLostCancelResponse()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new UploadServer(fixture) { LoseFirstCreate = true, LoseFirstCancel = true };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("example.bin", source);
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(item.Id));
        Assert.Null(Assert.Single(await outbox.ListAsync()).ServerUploadId);

        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.RemoveAsync(item.Id));
        var persisted = Assert.Single(await outbox.ListAsync());
        Assert.NotNull(persisted.ServerUploadId);
        Assert.Equal(OutboxState.Uploading, persisted.State);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
        Assert.Empty(server.PutNumbers);
        Assert.Equal(1, server.Creates);
        Assert.Equal([item.Id, item.Id], server.CreateKeys);

        var restartedClient = fixture.NewClient();
        await restartedClient.ConfigureAsync(fixture.Scope);
        var restarted = new UploadOutbox(directory.Path, restartedClient);
        await restarted.RemoveAsync(item.Id);
        Assert.Empty(await restarted.ListAsync());
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
        Assert.Equal(2, server.Cancels);
        Assert.Equal(1, server.Creates);
    }

    [Fact]
    public async Task RemoveRecoversAmbiguousCreationButKeepsLocalItemWhenCancellationConflicts()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new UploadServer(fixture) { LoseFirstCreate = true, CancellationConflicts = true };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("example.bin", source);
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(item.Id));

        server.MarkFinalizing();
        var failure = await Assert.ThrowsAsync<NexoraApiException>(() => outbox.RemoveAsync(item.Id));
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        var recovered = Assert.Single(await outbox.ListAsync());
        Assert.NotNull(recovered.ServerUploadId);
        Assert.Equal(OutboxState.Finalizing, recovered.State);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
        Assert.Empty(server.PutNumbers);
        Assert.Equal(1, server.Creates);
        Assert.Equal([item.Id, item.Id], server.CreateKeys);
    }

    [Fact]
    public async Task RemovePendingItemDeletesOnlyLocalDataWithoutCreatingRemoteReservation()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var requests = 0;
        fixture.Handler.Handle = (_, _) => { requests++; throw new InvalidOperationException("No request expected"); };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("example.bin", source);

        await outbox.RemoveAsync(item.Id);
        Assert.Empty(await outbox.ListAsync());
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
        Assert.Equal(0, requests);
    }

    private sealed class UploadServer
    {
        private readonly ClientFixture fixture;
        private Guid id = Guid.NewGuid();
        private CreateUploadRequest? request;
        private UploadState state;
        internal bool LoseFirstChunk { get; set; }
        internal bool LoseFirstCreate { get; set; }
        internal bool LoseFirstCancel { get; set; }
        internal bool CancellationConflicts { get; set; }
        internal int Creates { get; private set; }
        internal int Cancels { get; private set; }
        internal HashSet<int> Chunks { get; } = [];
        internal List<int> PutNumbers { get; } = [];
        internal List<Guid?> CreateKeys { get; } = [];
        internal UploadServer(ClientFixture fixture)
        {
            this.fixture = fixture;
            fixture.Handler.Handle = HandleAsync;
        }
        internal void RestoreBeforeUpload()
        {
            id = Guid.NewGuid();
            request = null;
            state = UploadState.Open;
            Chunks.Clear();
        }
        internal void MarkFinalizing() => state = UploadState.Finalizing;
        private async Task<System.Net.Http.HttpResponseMessage> HandleAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            var path = message.RequestUri!.AbsolutePath;
            if (message.Method == HttpMethod.Post && path == "/api/uploads")
            {
                var posted = (await message.Content!.ReadFromJsonAsync<CreateUploadRequest>(cancellationToken))!;
                CreateKeys.Add(posted.ClientRequestId);
                if (request is null) { request = posted; Creates++; }
                else Assert.Equal(request, posted);
                if (LoseFirstCreate) { LoseFirstCreate = false; throw new HttpRequestException("Lost create response"); }
                return MockHandler.Json(Snapshot());
            }
            if (message.Method == HttpMethod.Get)
                return path == $"/api/uploads/{id:D}" && request is not null ? MockHandler.Json(Snapshot())
                    : MockHandler.Json(new { code = "resource_not_found" }, HttpStatusCode.NotFound);
            if (message.Method == HttpMethod.Delete)
            {
                Cancels++;
                if (path != $"/api/uploads/{id:D}" || request is null)
                    return MockHandler.Json(new { code = "resource_not_found" }, HttpStatusCode.NotFound);
                if (CancellationConflicts)
                    return MockHandler.Json(new { code = "upload_not_open" }, HttpStatusCode.Conflict);
                state = UploadState.Cancelled;
                if (LoseFirstCancel) { LoseFirstCancel = false; throw new HttpRequestException("Lost cancel response"); }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (message.Method == HttpMethod.Put)
            {
                var number = int.Parse(path.Split('/')[^1]);
                var bytes = await message.Content!.ReadAsByteArrayAsync(cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                Assert.Equal(hash, message.Headers.GetValues("X-Chunk-SHA256").Single());
                Assert.Equal(bytes.Length, message.Content.Headers.ContentLength);
                PutNumbers.Add(number);
                Chunks.Add(number);
                if (LoseFirstChunk) { LoseFirstChunk = false; throw new HttpRequestException("Lost chunk response"); }
                return MockHandler.Json(new UploadChunkReceipt(number, bytes.Length, hash, false));
            }
            if (path.EndsWith("/complete", StringComparison.Ordinal))
            {
                state = UploadState.Completed;
                return MockHandler.Json(Snapshot());
            }
            throw new InvalidOperationException("Unexpected request");
        }
        private UploadSnapshot Snapshot() => new(id, request!.OriginalName, request.ExpectedLength, 4,
            (int)((request.ExpectedLength + 3) / 4), state, Chunks.Order().ToArray(), fixture.Clock.Now,
            fixture.Clock.Now, state == UploadState.Completed ? new AssetSnapshot(Guid.NewGuid(), request.OriginalName,
                request.ExpectedLength, "application/octet-stream", fixture.Clock.Now, false, null) : null, null, null);
    }
}
