namespace Nexora.Mobile.Core;

public enum TransferStage
{
    Idle, Preparing, Uploading, Finalizing, WaitingForServer, Completed, Paused, Failed, LoginRequired
}

public sealed record TransferSnapshot(Guid? ItemId, string? ScopeKey, TransferStage Stage,
    UploadProgress? Progress = null, string? FailureCode = null, string? LibrarySyncFailureCode = null)
{
    public bool IsRunning => Stage is TransferStage.Preparing or TransferStage.Uploading or TransferStage.Finalizing;
}

// The platform owns the foreground service. This coordinator has no scheduler,
// automatic restart or retry policy: one explicit run sends one queued item.
public sealed class UploadTransferCoordinator
{
    private readonly NexoraClient client;
    private readonly UploadOutbox outbox;
    private readonly LocalLibraryCache library;
    private readonly TimeProvider clock;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan finalizationTimeout;
    private readonly object gate = new();
    private TransferSnapshot current = new(null, null, TransferStage.Idle);
    private ActiveRun? active;
    private Action<TransferSnapshot>? changed;

    public UploadTransferCoordinator(NexoraClient client, UploadOutbox outbox, LocalLibraryCache library,
        TimeProvider? clock = null, TimeSpan? finalizationPollInterval = null, TimeSpan? finalizationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(library);
        this.client = client;
        this.outbox = outbox;
        this.library = library;
        this.clock = clock ?? TimeProvider.System;
        pollInterval = finalizationPollInterval ?? TimeSpan.FromSeconds(2);
        this.finalizationTimeout = finalizationTimeout ?? TimeSpan.FromMinutes(2);
        if (pollInterval <= TimeSpan.Zero || pollInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(finalizationPollInterval));
        if (this.finalizationTimeout <= TimeSpan.Zero || this.finalizationTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(finalizationTimeout));
    }

    public TransferSnapshot Current { get { lock (gate) return current; } }
    public event Action<TransferSnapshot>? Changed
    {
        add { lock (gate) changed += value; }
        remove { lock (gate) changed -= value; }
    }

    public Task RunAsync(Guid itemId, string expectedScopeKey, CancellationToken cancellationToken = default)
    {
        if (itemId == Guid.Empty) throw new ArgumentException("Informe o item da fila.", nameof(itemId));
        if (string.IsNullOrEmpty(expectedScopeKey)) throw new ArgumentException("Informe a conta selecionada.", nameof(expectedScopeKey));
        ActiveRun run;
        lock (gate)
        {
            if (active is not null) throw new InvalidOperationException("Já existe uma transferência em andamento.");
            run = new ActiveRun(itemId, expectedScopeKey, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            active = run;
        }
        Publish(run, new(itemId, expectedScopeKey, TransferStage.Preparing));
        return ExecuteAsync(run);
    }

    public void RequestPause()
    {
        ActiveRun? run;
        lock (gate) run = active;
        Cancel(run);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ActiveRun? run;
        lock (gate) run = active;
        Cancel(run);
        if (run is not null) await run.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Cancel(ActiveRun? run)
    {
        try { run?.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { } // The captured run already became idle.
    }

    private async Task ExecuteAsync(ActiveRun run)
    {
        TransferSnapshot terminal;
        try
        {
            terminal = await client.ExecuteInScopeAsync(async () =>
            {
                if (client.Scope?.Key != run.ScopeKey)
                    return Snapshot(run, TransferStage.Failed, "scope_changed");
                run.Cancellation.Token.ThrowIfCancellationRequested();
                var progress = new InlineProgress(value => Publish(run,
                    new(run.ItemId, run.ScopeKey, TransferStage.Uploading, value)));
                var item = await outbox.ResumeAsync(run.ItemId, run.Cancellation.Token, progress);
                if (item.State == OutboxState.Finalizing)
                {
                    Publish(run, Snapshot(run, TransferStage.Finalizing));
                    var started = clock.GetTimestamp();
                    using var deadline = new CancellationTokenSource(finalizationTimeout, clock);
                    using var polling = CancellationTokenSource.CreateLinkedTokenSource(run.Cancellation.Token, deadline.Token);
                    try
                    {
                        while (item.State == OutboxState.Finalizing)
                        {
                            var remaining = finalizationTimeout - clock.GetElapsedTime(started);
                            if (remaining <= TimeSpan.Zero) return Snapshot(run, TransferStage.WaitingForServer);
                            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, clock, polling.Token);
                            if (clock.GetElapsedTime(started) >= finalizationTimeout)
                                return Snapshot(run, TransferStage.WaitingForServer);
                            item = await outbox.ResumeAsync(run.ItemId, polling.Token, progress);
                        }
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested && !run.Cancellation.IsCancellationRequested)
                    {
                        return Snapshot(run, TransferStage.WaitingForServer);
                    }
                }
                if (item.State != OutboxState.Completed)
                    return Snapshot(run, TransferStage.Failed, SafeCode(item.FailureCode, "upload_failed"));

                // Keep the scope lease through the cache commit. A later sync error
                // does not change the already durable server upload into a failure.
                string? syncFailure = null;
                try { await library.SynchronizeAsync(run.Cancellation.Token); }
                catch (Exception error) { syncFailure = FailureCode(error, run.Cancellation.Token); }
                return Snapshot(run, TransferStage.Completed) with { LibrarySyncFailureCode = syncFailure };
            }, run.Cancellation.Token);
        }
        catch (LoginRequiredException)
        {
            terminal = Snapshot(run, TransferStage.LoginRequired, "login_required");
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            terminal = Snapshot(run, TransferStage.Paused);
        }
        catch (Exception error)
        {
            terminal = Snapshot(run, TransferStage.Failed, FailureCode(error, run.Cancellation.Token));
        }

        // Idle means all network/local operations and the enclosing scope lease
        // have finished, so logout and account changes can now proceed safely.
        Action<TransferSnapshot>? subscribers;
        lock (gate)
        {
            current = terminal;
            subscribers = changed;
        }
        run.Cancellation.Dispose();
        Notify(subscribers, terminal);
        // Reserve the run until its terminal event has been delivered. An old
        // completion observer must not stop notifications for a newer transfer.
        lock (gate) active = null;
        run.Completion.TrySetResult();
    }

    private TransferSnapshot Snapshot(ActiveRun run, TransferStage stage, string? failureCode = null)
    {
        lock (gate) return new(run.ItemId, run.ScopeKey, stage, current.Progress, failureCode);
    }

    private void Publish(ActiveRun run, TransferSnapshot snapshot)
    {
        Action<TransferSnapshot>? subscribers;
        lock (gate)
        {
            if (!ReferenceEquals(active, run)) return;
            current = snapshot;
            subscribers = changed;
        }
        Notify(subscribers, snapshot);
    }

    private static void Notify(Action<TransferSnapshot>? subscribers, TransferSnapshot snapshot)
    {
        if (subscribers is null) return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action<TransferSnapshot>>())
        {
            try { subscriber(snapshot); }
            catch (Exception) { } // UI/notification observers cannot interrupt data transfer.
        }
    }

    private static string FailureCode(Exception error, CancellationToken cancellationToken) => error switch
    {
        LoginRequiredException => "login_required",
        OperationCanceledException when cancellationToken.IsCancellationRequested => "paused",
        OperationCanceledException or HttpRequestException => "network_unavailable",
        NexoraApiException api => SafeCode(api.Code, "server_rejected"),
        InvalidDataException or System.Text.Json.JsonException => "invalid_upload_data",
        IOException or UnauthorizedAccessException => "local_storage_unavailable",
        _ => "transfer_failed"
    };

    private static string SafeCode(string? value, string fallback) =>
        value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? value : fallback;

    private sealed class ActiveRun(Guid itemId, string scopeKey, CancellationTokenSource cancellation)
    {
        internal Guid ItemId { get; } = itemId;
        internal string ScopeKey { get; } = scopeKey;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class InlineProgress(Action<UploadProgress> report) : IProgress<UploadProgress>
    {
        public void Report(UploadProgress value) => report(value);
    }
}
