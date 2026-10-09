using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading.Channels;
using Nexora.Mobile.Core;

namespace Nexora.Mobile.Tests;

public sealed class TransferCoordinatorTests
{
    [Fact]
    public async Task PauseKeepsConfirmedChunksAndRestartSendsOnlyRemainingBytes()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new TransferServer(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforePut = async (number, token) =>
        {
            if (number != 1) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var cache = new LocalLibraryCache(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, cache);
        using var source = new MemoryStream([1, 2, 3, 4, 5, 6, 7]);
        var item = await outbox.EnqueueAsync("private.bin", source);
        var running = coordinator.RunAsync(item.Id, fixture.Scope.Key);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.Current.IsRunning);
        Assert.Equal(TransferStage.Uploading, coordinator.Current.Stage);
        Assert.Equal(4, coordinator.Current.Progress!.ConfirmedBytes);
        Assert.Throws<InvalidOperationException>(() => { _ = coordinator.RunAsync(item.Id, fixture.Scope.Key); });

        await coordinator.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await running;
        Assert.Equal(TransferStage.Paused, coordinator.Current.Stage);
        Assert.False(coordinator.Current.IsRunning);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
        var paused = Assert.Single(await outbox.ListAsync());
        Assert.Equal([0], paused.ConfirmedChunks);

        server.BeforePut = null;
        await coordinator.RunAsync(item.Id, fixture.Scope.Key);
        Assert.Equal(TransferStage.Completed, coordinator.Current.Stage);
        Assert.Null(coordinator.Current.FailureCode);
        Assert.Null(coordinator.Current.LibrarySyncFailureCode);
        Assert.Equal([0, 1, 1], server.AttemptedChunks);
        Assert.Equal(1, server.Creates);
        Assert.Single((await cache.GetCachedAsync()).Items);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
    }

    [Fact]
    public async Task ScopeChangeWaitsForTransferAndLaterKickoffWithOldKeySendsNothing()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new TransferServer(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforePut = async (_, token) =>
        {
            entered.TrySetResult();
            await continuation.Task.WaitAsync(token);
        };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var cache = new LocalLibraryCache(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, cache);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("private.bin", source);
        var running = coordinator.RunAsync(item.Id, fixture.Scope.Key);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = ServerScope.Create("https://other.example.test", "bob");
        var changing = fixture.Client.ConfigureAsync(other);
        Assert.False(changing.IsCompleted);
        continuation.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        await changing.WaitAsync(TimeSpan.FromSeconds(5));
        var requestsBefore = server.Requests.Count;
        await coordinator.RunAsync(item.Id, fixture.Scope.Key);
        Assert.Equal(TransferStage.Failed, coordinator.Current.Stage);
        Assert.Equal("scope_changed", coordinator.Current.FailureCode);
        Assert.Equal(requestsBefore, server.Requests.Count);
        Assert.All(server.Requests, uri => Assert.Equal(fixture.Scope.Server.Host, uri.Host));
        Assert.Empty((await cache.GetCachedAsync()).Items);
    }

    [Fact]
    public async Task RevokedSessionStopsWithoutDeletingSourceOrAutomaticallyRetrying()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new TransferServer(fixture) { RejectChunks = true };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, new LocalLibraryCache(directory.Path, fixture.Client));
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("private.bin", source);

        await coordinator.RunAsync(item.Id, fixture.Scope.Key);
        Assert.Equal(TransferStage.LoginRequired, coordinator.Current.Stage);
        Assert.Equal("login_required", coordinator.Current.FailureCode);
        Assert.False(fixture.Client.IsSignedIn);
        Assert.Single(server.AttemptedChunks);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
        Assert.Single(await outbox.ListAsync());
    }

    [Fact]
    public async Task FinalizationPollingStopsAtDeadlineAndNextManualRunCanComplete()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new TransferServer(fixture) { FinalizationReadsUntilComplete = int.MaxValue };
        var clock = new ManualTransferClock();
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, new LocalLibraryCache(directory.Path, fixture.Client),
            clock, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6));
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("private.bin", source);
        var running = coordinator.RunAsync(item.Id, fixture.Scope.Key);
        await clock.TickNextDelayAsync();
        await clock.TickNextDelayAsync();
        await clock.TickNextDelayAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TransferStage.WaitingForServer, coordinator.Current.Stage);
        Assert.False(coordinator.Current.IsRunning);
        Assert.Equal(2, server.FinalizationReads);
        Assert.Equal(OutboxState.Finalizing, Assert.Single(await outbox.ListAsync()).State);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
        server.FinalizationReadsUntilComplete = 0;
        await coordinator.RunAsync(item.Id, fixture.Scope.Key);
        Assert.Equal(TransferStage.Completed, coordinator.Current.Stage);
        Assert.Equal(1, server.Creates);
        Assert.Single(server.AttemptedChunks);
    }

    [Fact]
    public async Task PauseInterruptsFinalizationTimerWithoutDeletingLocalData()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new TransferServer(fixture) { FinalizationReadsUntilComplete = int.MaxValue };
        var clock = new ManualTransferClock();
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, new LocalLibraryCache(directory.Path, fixture.Client), clock);
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("private.bin", source);
        var running = coordinator.RunAsync(item.Id, fixture.Scope.Key);
        await clock.WaitForDelayAsync();
        Assert.Equal(TransferStage.Finalizing, coordinator.Current.Stage);
        coordinator.RequestPause();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TransferStage.Paused, coordinator.Current.Stage);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
        Assert.Equal(0, server.FinalizationReads);
    }

    [Fact]
    public async Task FinalizationDeadlineCancelsStalledHttpPollAndReportsWaiting()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new TransferServer(fixture) { FinalizationReadsUntilComplete = int.MaxValue };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforePoll = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var clock = new ManualTransferClock();
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, new LocalLibraryCache(directory.Path, fixture.Client),
            clock, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6));
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("private.bin", source);
        var running = coordinator.RunAsync(item.Id, fixture.Scope.Key);
        await clock.TickNextDelayAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.AdvanceBy(TimeSpan.FromSeconds(4));
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TransferStage.WaitingForServer, coordinator.Current.Stage);
        Assert.Null(coordinator.Current.FailureCode);
        Assert.True(fixture.Client.IsSignedIn);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
    }

    [Theory]
    [InlineData(false, "network_unavailable")]
    [InlineData(true, "login_required")]
    public async Task LibrarySyncFailureDoesNotMisreportCompletedUpload(bool revoked, string expectedFailure)
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new TransferServer(fixture) { FailSync = !revoked, RejectSync = revoked };
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, new LocalLibraryCache(directory.Path, fixture.Client));
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("private.bin", source);
        coordinator.Changed += _ => throw new InvalidOperationException("A UI observer failed");
        var stages = new List<TransferStage>();
        coordinator.Changed += snapshot => stages.Add(snapshot.Stage);

        await coordinator.RunAsync(item.Id, fixture.Scope.Key);
        Assert.Equal(TransferStage.Completed, coordinator.Current.Stage);
        Assert.Null(coordinator.Current.FailureCode);
        Assert.Equal(expectedFailure, coordinator.Current.LibrarySyncFailureCode);
        Assert.Equal(TransferStage.Completed, stages[^1]);
        Assert.Equal(OutboxState.Completed, Assert.Single(await outbox.ListAsync()).State);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));
    }

    [Fact]
    public async Task NetworkFailurePreservesOutboxAndNeedsExplicitNewRun()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new TransferServer(fixture);
        server.BeforePut = (_, _) => throw new HttpRequestException("secret filename and remote response");
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, new LocalLibraryCache(directory.Path, fixture.Client));
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("private.bin", source);
        await coordinator.RunAsync(item.Id, fixture.Scope.Key);
        Assert.Equal(TransferStage.Failed, coordinator.Current.Stage);
        Assert.Equal("network_unavailable", coordinator.Current.FailureCode);
        Assert.Single(server.AttemptedChunks);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, item.SourcePath)));

        server.BeforePut = null;
        await coordinator.RunAsync(item.Id, fixture.Scope.Key);
        Assert.Equal(TransferStage.Completed, coordinator.Current.Stage);
        Assert.Equal(1, server.Creates);
        Assert.Equal([0, 0], server.AttemptedChunks);
    }

    [Fact]
    public async Task ChangedPrivateSourceFailsBeforeAnyHttpRequestWithSafeReason()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var server = new TransferServer(fixture);
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, new LocalLibraryCache(directory.Path, fixture.Client));
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("private.bin", source);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, item.SourcePath), [9, 9, 9]);
        await coordinator.RunAsync(item.Id, fixture.Scope.Key);
        Assert.Equal(TransferStage.Failed, coordinator.Current.Stage);
        Assert.Equal("invalid_upload_data", coordinator.Current.FailureCode);
        Assert.Empty(server.Requests);
        Assert.Single(await outbox.ListAsync());
    }

    [Fact]
    public async Task CompletionObserversCannotStartAnotherRunBeforeTerminalEventIsDelivered()
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        _ = new TransferServer(fixture);
        await fixture.InitializeAsync();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var coordinator = new UploadTransferCoordinator(fixture.Client, outbox, new LocalLibraryCache(directory.Path, fixture.Client));
        using var source = new MemoryStream([1, 2, 3]);
        var item = await outbox.EnqueueAsync("private.bin", source);
        Exception? reentrantStart = null;
        coordinator.Changed += snapshot =>
        {
            if (snapshot.Stage != TransferStage.Completed) return;
            try { _ = coordinator.RunAsync(Guid.NewGuid(), fixture.Scope.Key); }
            catch (Exception error) { reentrantStart = error; }
        };
        TransferSnapshot? observedCompletion = null;
        coordinator.Changed += snapshot =>
        {
            if (snapshot.Stage == TransferStage.Completed) observedCompletion = coordinator.Current;
        };
        await coordinator.RunAsync(item.Id, fixture.Scope.Key);
        Assert.IsType<InvalidOperationException>(reentrantStart);
        Assert.Equal(item.Id, observedCompletion!.ItemId);
        Assert.Equal(TransferStage.Completed, observedCompletion.Stage);
        await coordinator.RunAsync(item.Id, "different-scope");
        Assert.Equal(TransferStage.Failed, coordinator.Current.Stage);
        Assert.Equal("scope_changed", coordinator.Current.FailureCode);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(61000, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 600001)]
    public void TimingControlsRejectUnboundedOrInvalidValues(int pollMilliseconds, int timeoutMilliseconds)
    {
        using var directory = new PrivateDirectory();
        var fixture = new ClientFixture();
        var outbox = new UploadOutbox(directory.Path, fixture.Client);
        var library = new LocalLibraryCache(directory.Path, fixture.Client);
        Assert.Throws<ArgumentOutOfRangeException>(() => new UploadTransferCoordinator(fixture.Client, outbox, library,
            finalizationPollInterval: TimeSpan.FromMilliseconds(pollMilliseconds),
            finalizationTimeout: TimeSpan.FromMilliseconds(timeoutMilliseconds)));
    }

    private sealed class TransferServer
    {
        private readonly ClientFixture fixture;
        private readonly Guid id = Guid.NewGuid();
        private readonly Guid assetId = Guid.NewGuid();
        private CreateUploadRequest? request;
        private UploadState state;
        private readonly HashSet<int> chunks = [];
        internal Func<int, CancellationToken, Task>? BeforePut { get; set; }
        internal Func<CancellationToken, Task>? BeforePoll { get; set; }
        internal bool RejectChunks { get; init; }
        internal bool RejectSync { get; init; }
        internal bool FailSync { get; init; }
        internal int FinalizationReadsUntilComplete { get; set; }
        internal int FinalizationReads { get; private set; }
        internal int Creates { get; private set; }
        internal List<int> AttemptedChunks { get; } = [];
        internal List<Uri> Requests { get; } = [];
        internal TransferServer(ClientFixture fixture)
        {
            this.fixture = fixture;
            fixture.Handler.Handle = HandleAsync;
        }
        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage message, CancellationToken token)
        {
            Requests.Add(message.RequestUri!);
            var path = message.RequestUri!.AbsolutePath;
            if (path == "/api/sync")
            {
                if (FailSync) throw new HttpRequestException("Secret network diagnostic");
                if (RejectSync) return MockHandler.Json(new { code = "unauthorized" }, HttpStatusCode.Unauthorized);
                return MockHandler.Json(new SyncSnapshotPage([Asset()], null, "synced"));
            }
            if (message.Method == HttpMethod.Post && path == "/api/uploads")
            {
                request = (await message.Content!.ReadFromJsonAsync<CreateUploadRequest>(token))!;
                Creates++;
                return MockHandler.Json(Snapshot());
            }
            if (message.Method == HttpMethod.Get)
            {
                if (BeforePoll is not null) await BeforePoll(token);
                if (state == UploadState.Finalizing && ++FinalizationReads >= FinalizationReadsUntilComplete)
                    state = UploadState.Completed;
                return MockHandler.Json(Snapshot());
            }
            if (message.Method == HttpMethod.Put)
            {
                var number = int.Parse(path.Split('/')[^1]);
                AttemptedChunks.Add(number);
                if (RejectChunks) return MockHandler.Json(new { code = "unauthorized" }, HttpStatusCode.Unauthorized);
                if (BeforePut is not null) await BeforePut(number, token);
                var bytes = await message.Content!.ReadAsByteArrayAsync(token);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                Assert.Equal(hash, message.Headers.GetValues("X-Chunk-SHA256").Single());
                chunks.Add(number);
                return MockHandler.Json(new UploadChunkReceipt(number, bytes.Length, hash, false));
            }
            if (path.EndsWith("/complete", StringComparison.Ordinal))
            {
                state = FinalizationReadsUntilComplete == 0 ? UploadState.Completed : UploadState.Finalizing;
                return MockHandler.Json(Snapshot());
            }
            throw new InvalidOperationException("Unexpected request");
        }
        private AssetSnapshot Asset() => new(assetId, request!.OriginalName, request.ExpectedLength,
            "application/octet-stream", fixture.Clock.Now, false, null);
        private UploadSnapshot Snapshot() => new(id, request!.OriginalName, request.ExpectedLength, 4,
            (int)((request.ExpectedLength + 3) / 4), state, chunks.Order().ToArray(), fixture.Clock.Now, fixture.Clock.Now,
            state == UploadState.Completed ? Asset() : null, null, null);
    }

    private sealed class ManualTransferClock : TimeProvider
    {
        private long ticks;
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private readonly Channel<ManualTimer> scheduled = Channel.CreateUnbounded<ManualTimer>();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ManualTimer(callback, state, dueTime, GetTimestamp() + dueTime.Ticks);
            lock (gate) timers.Add(timer);
            Assert.True(scheduled.Writer.TryWrite(timer));
            return timer;
        }
        internal async Task TickNextDelayAsync()
        {
            var timer = await NextPollingDelayAsync();
            AdvanceBy(TimeSpan.FromTicks(timer.DueTimestamp - GetTimestamp()));
        }
        internal async Task WaitForDelayAsync() => _ = await NextPollingDelayAsync();
        private async Task<ManualTimer> NextPollingDelayAsync()
        {
            while (true)
            {
                var timer = await scheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (timer.Delay <= TimeSpan.FromSeconds(2)) return timer;
            }
        }
        internal void AdvanceBy(TimeSpan elapsed)
        {
            var timestamp = Interlocked.Add(ref ticks, elapsed.Ticks);
            ManualTimer[] due;
            lock (gate) due = timers.Where(timer => timer.DueTimestamp <= timestamp).OrderBy(timer => timer.DueTimestamp).ToArray();
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan delay, long dueTimestamp) : ITimer
        {
            private int disposed;
            private int fired;
            internal TimeSpan Delay { get; } = delay;
            internal long DueTimestamp { get; } = dueTimestamp;
            internal void Fire() { if (Volatile.Read(ref disposed) == 0 && Interlocked.Exchange(ref fired, 1) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
