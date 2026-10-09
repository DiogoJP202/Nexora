using Android.Content;
using Nexora.Mobile.Core;

namespace Nexora.Mobile;

public sealed class AndroidUploadTransferRunner(NexoraClient client, UploadTransferCoordinator coordinator)
    : IUploadTransferRunner
{
    private readonly object gate = new();
    private LaunchRequest? request;
    public event Action? StateChanged;

    public bool IsRunning { get { lock (gate) return request is not null || coordinator.Current.IsRunning; } }

    public async Task StartAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (itemId == Guid.Empty) throw new ArgumentException("Informe o item da fila.", nameof(itemId));
        if (!MainThread.IsMainThread || !MainActivity.IsVisible)
            throw new InvalidOperationException("Abra o Nexora para iniciar o envio.");
        cancellationToken.ThrowIfCancellationRequested();
        // Android allows the foreground service even when notifications are
        // denied. Request contextually; denial leaves pause available in the app.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            try
            {
                if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                    await Permissions.RequestAsync<Permissions.PostNotifications>();
            }
            catch (PermissionException) { }
            // Permission result can precede OnResume. Wait briefly for the
            // activity rather than treating a denial as a background start.
            await MainActivity.WaitUntilVisibleAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!MainActivity.IsVisible) throw new InvalidOperationException("Volte ao Nexora para iniciar o envio.");
        var scope = client.Scope ?? throw new LoginRequiredException();
        if (!client.IsSignedIn) throw new LoginRequiredException();
        LaunchRequest launch;
        lock (gate)
        {
            if (request is not null || coordinator.Current.IsRunning)
                throw new InvalidOperationException("Já existe um envio em andamento.");
            launch = new(itemId, scope.Key);
            request = launch;
        }
        NotifyStateChanged();
        try
        {
            // A single-use in-memory ticket prevents a delayed/replayed intent
            // from restarting work after pause, logout or account configuration.
            var intent = new Intent(Platform.AppContext, typeof(UploadForegroundService))
                .SetAction(UploadForegroundService.StartAction)
                .PutExtra(UploadForegroundService.TicketExtra, launch.Ticket.ToString("N"));
            var component = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? Platform.AppContext.StartForegroundService(intent)
                : Platform.AppContext.StartService(intent);
            if (component is null) throw new InvalidOperationException("Não foi possível iniciar o envio. Retome pela fila.");
        }
        catch (Exception)
        {
            Cancel(launch);
            Complete(launch);
            throw new InvalidOperationException("Não foi possível iniciar o envio. A cópia continua na fila.");
        }
        // Native promotion may fail after StartForegroundService returned. Wait
        // for that acknowledgement so the visible UI can report the rejection.
        // Cancelling this page wait does not cancel the service's own lifetime.
        await launch.Started.Task.WaitAsync(cancellationToken);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        LaunchRequest? launch;
        lock (gate) launch = request;
        Cancel(launch);
        coordinator.RequestPause();
        await coordinator.PauseAsync(cancellationToken);
        // Even a cancelled, not-yet-delivered start retains admission until its
        // native service has consumed the ticket and finished destruction.
        if (launch is not null) await launch.Completion.Task.WaitAsync(cancellationToken);
    }

    internal LaunchRequest? Claim(Guid ticket)
    {
        lock (gate)
        {
            if (request is null || request.Ticket != ticket || request.Claimed)
                return null;
            request.Claimed = true;
            return request;
        }
    }

    internal void Complete(LaunchRequest launch)
    {
        lock (gate) if (ReferenceEquals(request, launch)) request = null;
        launch.Completion.TrySetResult();
        launch.Cancellation.Dispose();
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        if (StateChanged is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch (Exception) { }
        }
    }

    internal static void Cancel(LaunchRequest? launch)
    {
        try { launch?.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal sealed class LaunchRequest(Guid itemId, string scopeKey)
    {
        internal Guid Ticket { get; } = Guid.NewGuid();
        internal Guid ItemId { get; } = itemId;
        internal string ScopeKey { get; } = scopeKey;
        internal bool Claimed { get; set; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
