using System.Text.Json;
using Nexora.Mobile.Core;

namespace Nexora.Mobile;

// Credentials stay in Android's encrypted SecureStorage, never in library/outbox JSON.
public sealed class SecureSessionStore : ISecureSessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Key(ServerScope scope) => "nexora.session." + scope.Key;

    public async Task<SecureSession?> GetAsync(ServerScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var value = await SecureStorage.Default.GetAsync(Key(scope));
            cancellationToken.ThrowIfCancellationRequested();
            if (value is null) return null;
            if (value.Length > 40 * 1024) throw new JsonException();
            return JsonSerializer.Deserialize<SecureSession>(value, Json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Invalidated keystore entries require a fresh login, including after reinstall/transfer.
            try { SecureStorage.Default.Remove(Key(scope)); } catch (Exception) { }
            return null;
        }
    }

    public async Task SaveAsync(ServerScope scope, SecureSession session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { await SecureStorage.Default.SetAsync(Key(scope), JsonSerializer.Serialize(session, Json)); }
        catch (Exception) { throw new IOException("Não foi possível proteger a sessão neste dispositivo."); }
    }

    public Task ClearAsync(ServerScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { SecureStorage.Default.Remove(Key(scope)); }
        catch (Exception) { throw new IOException("Não foi possível limpar a sessão protegida."); }
        return Task.CompletedTask;
    }
}
