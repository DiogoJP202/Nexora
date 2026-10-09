namespace Nexora.Mobile.Core;

// Reader leases protect the selected account without serializing its requests.
// Queued configuration changes precede later readers, while nested operations
// retain the enclosing lease until their last reference has finished.
internal sealed class ScopeLeaseGate
{
    private readonly object gate = new();
    private readonly Queue<Admission> waiting = new();
    private int readers;
    private bool writer;

    internal Task<IDisposable> ReadAsync(CancellationToken cancellationToken) => EnterAsync(false, cancellationToken);
    internal Task<IDisposable> WriteAsync(CancellationToken cancellationToken) => EnterAsync(true, cancellationToken);

    private Task<IDisposable> EnterAsync(bool exclusive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!writer && waiting.Count == 0 && (!exclusive || readers == 0))
                return Task.FromResult(Grant(exclusive));
            var admission = new Admission(exclusive);
            waiting.Enqueue(admission);
            return WaitAsync(admission, cancellationToken);
        }
    }

    private async Task<IDisposable> WaitAsync(Admission admission, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.UnsafeRegister(_ => Cancel(admission, cancellationToken), null);
        return await admission.Completion.Task.ConfigureAwait(false);
    }

    private void Cancel(Admission admission, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!admission.Pending) return;
            admission.Pending = false;
            admission.Completion.TrySetCanceled(cancellationToken);
            Drain();
        }
    }

    private IDisposable Grant(bool exclusive)
    {
        if (exclusive) writer = true;
        else readers++;
        return new Lease(() => Release(exclusive));
    }

    private void Release(bool exclusive)
    {
        lock (gate)
        {
            if (exclusive) writer = false;
            else readers--;
            Drain();
        }
    }

    private void Drain()
    {
        while (!writer && waiting.TryPeek(out var next))
        {
            if (!next.Pending) { waiting.Dequeue(); continue; }
            if (next.Exclusive && readers != 0) return;
            waiting.Dequeue();
            next.Pending = false;
            next.Completion.TrySetResult(Grant(next.Exclusive));
        }
    }

    private sealed class Admission(bool exclusive)
    {
        internal bool Exclusive { get; } = exclusive;
        internal bool Pending { get; set; } = true;
        internal TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
