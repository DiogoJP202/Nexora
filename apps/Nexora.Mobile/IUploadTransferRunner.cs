namespace Nexora.Mobile;

// A platform runner owns the lifetime beyond the current page. Starting a run
// is an explicit visible-user action; pausing preserves the durable outbox.
public interface IUploadTransferRunner
{
    bool IsRunning { get; }
    event Action? StateChanged;
    Task StartAsync(Guid itemId, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
}
