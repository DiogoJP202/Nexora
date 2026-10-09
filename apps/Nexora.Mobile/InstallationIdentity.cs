using Nexora.Mobile.Core;

namespace Nexora.Mobile;

// This UUID records an installation; it is not a hardware credential.
public sealed class InstallationIdentity : IInstallationIdentity
{
    private static string Key(ServerScope scope) => "nexora.device." + scope.Key;

    public Task<Guid?> GetDeviceIdAsync(ServerScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var saved = Preferences.Default.Get(Key(scope), "");
        return Task.FromResult(Guid.TryParse(saved, out var id) && id != Guid.Empty ? (Guid?)id : null);
    }

    public Task SetDeviceIdAsync(ServerScope scope, Guid deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deviceId == Guid.Empty) throw new ArgumentException("Identificação inválida.", nameof(deviceId));
        Preferences.Default.Set(Key(scope), deviceId.ToString("D"));
        return Task.CompletedTask;
    }

    public static void Forget(ServerScope scope) => Preferences.Default.Remove(Key(scope));
}
