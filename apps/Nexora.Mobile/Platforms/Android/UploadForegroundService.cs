using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Mobile.Core;

namespace Nexora.Mobile;

[Service(Name = "com.nexora.mobile.UploadForegroundService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class UploadForegroundService : Service
{
    internal const string StartAction = "com.nexora.mobile.START_UPLOAD";
    internal const string PauseAction = "com.nexora.mobile.PAUSE_UPLOAD";
    internal const string TicketExtra = "ticket";
    private const string ChannelId = "nexora_uploads";
    private const int NotificationId = 1001;
    private AndroidUploadTransferRunner runner = null!;
    private UploadTransferCoordinator coordinator = null!;
    private AndroidUploadTransferRunner.LaunchRequest? launch;
    private Task? transfer;
    private volatile bool stopping;
    private bool destroyed;
    private bool executionCompleted;
    private readonly object notificationGate = new();
    private long lastNotificationAt;
    private TransferStage? lastNotificationStage;

    public override void OnCreate()
    {
        base.OnCreate();
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("Aplicativo indisponível.");
        runner = services.GetRequiredService<AndroidUploadTransferRunner>();
        coordinator = services.GetRequiredService<UploadTransferCoordinator>();
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            using var channel = new NotificationChannel(ChannelId, "Envios", NotificationImportance.Low)
            { Description = "Envios iniciados no Nexora" };
            GetNotificationManager()?.CreateNotificationChannel(channel);
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (executionCompleted)
        {
            // Admission still belongs to this finished generation. Late
            // commands must not keep it alive while waiting for OnDestroy.
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (intent?.Action == PauseAction)
        {
            // Immutable actions belong to a generation. A queued click on an
            // older notification must never pause a later transfer.
            if (Guid.TryParseExact(intent.GetStringExtra(TicketExtra), "N", out var pauseTicket) &&
                launch?.Ticket == pauseTicket) StopImmediately();
            else if (transfer is null && !runner.IsRunning) StopSelfResult(startId);
            return StartCommandResult.NotSticky;
        }
        if (transfer is null && launch is null && intent?.Action == StartAction &&
            Guid.TryParseExact(intent.GetStringExtra(TicketExtra), "N", out var ticket)) launch = runner.Claim(ticket);
        // Claim is only an in-memory identity check. Promote before asynchronous
        // storage, authentication or network work, and handle OS rejection.
        try
        {
            using var notification = BuildNotification(coordinator.Current);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            else StartForeground(NotificationId, notification);
        }
        catch (Exception)
        {
            if (transfer is null && launch is { } rejected)
            {
                stopping = true;
                executionCompleted = true;
                AndroidUploadTransferRunner.Cancel(rejected);
                rejected.Started.TrySetException(new InvalidOperationException("O Android recusou o início. Retome pela fila."));
                StopSelfResult(startId);
            }
            else if (!runner.IsRunning) StopSelfResult(startId);
            return StartCommandResult.NotSticky;
        }

        if (transfer is not null) return StartCommandResult.NotSticky;
        if (launch is null)
        {
            // A rejected stale intent has no authority over another queued run.
            if (!runner.IsRunning)
            {
                StopForeground(StopForegroundFlags.Remove);
                StopSelfResult(startId);
            }
            return StartCommandResult.NotSticky;
        }
        launch.Started.TrySetResult();
        coordinator.Changed += OnTransferChanged;
        transfer = ExecuteTransferAsync(launch);
        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnTimeout(int startId, ForegroundService foregroundServiceType)
    {
        // Android grants only seconds here. Never await HTTP cancellation before
        // returning control to the OS; the outbox is already durable.
        StopImmediately();
    }

    public override void OnDestroy()
    {
        stopping = true;
        destroyed = true;
        coordinator.Changed -= OnTransferChanged;
        AndroidUploadTransferRunner.Cancel(launch);
        if (executionCompleted && launch is { } completed) runner.Complete(completed);
        base.OnDestroy();
    }

    private async Task ExecuteTransferAsync(AndroidUploadTransferRunner.LaunchRequest selected)
    {
        try
        {
            await coordinator.RunAsync(selected.ItemId, selected.ScopeKey, selected.Cancellation.Token);
        }
        catch (Exception)
        {
            // The durable queue survives platform startup failures. Never log
            // credentials, local paths, filenames or raw exception payloads.
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                coordinator.Changed -= OnTransferChanged;
                stopping = true;
                executionCompleted = true;
                if (!destroyed)
                {
                    StopForeground(StopForegroundFlags.Remove);
                    // No newer valid run can exist while this generation retains
                    // the runner. Stop even if another stale command is queued.
                    StopSelf();
                }
                // Keep cleanup and OnDestroy ordered on the native main thread.
                // A late destruction must never pause a newer process-local run.
                if (destroyed) runner.Complete(selected);
            });
        }
    }

    private void StopImmediately()
    {
        stopping = true;
        AndroidUploadTransferRunner.Cancel(launch);
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private void OnTransferChanged(TransferSnapshot snapshot)
    {
        if (stopping || !snapshot.IsRunning || snapshot.ItemId != launch?.ItemId) return;
        lock (notificationGate)
        {
            var now = System.Environment.TickCount64;
            if (lastNotificationStage == snapshot.Stage && now - lastNotificationAt < 1000) return;
            lastNotificationAt = now;
            lastNotificationStage = snapshot.Stage;
        }
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (stopping || snapshot.ItemId != launch?.ItemId) return;
            try
            {
                using var notification = BuildNotification(snapshot);
                GetNotificationManager()?.Notify(NotificationId, notification);
            }
            catch (Exception) { } // A cosmetic update cannot terminate the upload.
        });
    }

    private NotificationManager? GetNotificationManager() => GetSystemService(NotificationService) as NotificationManager;

    private Notification BuildNotification(TransferSnapshot snapshot)
    {
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        using (builder)
        {
            using var open = new Intent(this, typeof(MainActivity))
                .SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            using var openApp = PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            if (launch is { } selected)
            {
                using var pause = new Intent(this, typeof(UploadForegroundService)).SetAction(PauseAction)
                    .SetData(Android.Net.Uri.Parse("nexora-upload:" + selected.Ticket.ToString("N")))
                    .PutExtra(TicketExtra, selected.Ticket.ToString("N"));
                using var pauseUpload = PendingIntent.GetService(this, 1, pause, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                using var icon = Icon.CreateWithResource(this, Resource.Drawable.ic_upload);
                using var action = new Notification.Action.Builder(icon, "Pausar", pauseUpload).Build();
                builder.AddAction(action);
            }
            var finalizing = snapshot.Stage == TransferStage.Finalizing;
            var progress = snapshot.Progress;
            var percent = progress is { TotalBytes: > 0 }
                ? (int)Math.Clamp(progress.ConfirmedBytes * 100d / progress.TotalBytes, 0, 100) : 0;
            return builder.SetSmallIcon(Resource.Drawable.ic_upload)
                .SetContentTitle("Nexora · envio")
                .SetContentText(finalizing ? "Aguardando processamento no servidor" : "Enviando arquivo")
                .SetContentIntent(openApp).SetOngoing(true).SetOnlyAlertOnce(true)
                .SetVisibility(NotificationVisibility.Private)
                .SetProgress(100, percent, finalizing || progress is null).Build();
        }
    }
}
