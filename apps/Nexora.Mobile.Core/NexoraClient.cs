using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Nexora.Mobile.Core;

public sealed class NexoraClient
{
    private const int MaximumJsonBytes = 2 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly ISecureSessionStore sessions;
    private readonly IInstallationIdentity installation;
    private readonly TimeProvider clock;
    private readonly ScopeLeaseGate scopeGate = new();
    private readonly AsyncLocal<ScopeOperation?> scopeOperation = new();
    private readonly SemaphoreSlim authenticationGate = new(1, 1);
    private SecureSession? session;
    public ServerScope? Scope { get; private set; }
    public bool IsSignedIn => session is not null;

    // Supplied test transports must also disable redirects. Production should use CreateHttpClient.
    public NexoraClient(HttpClient http, ISecureSessionStore sessions, IInstallationIdentity installation, TimeProvider? clock = null)
    {
        this.http = http;
        this.sessions = sessions;
        this.installation = installation;
        this.clock = clock ?? TimeProvider.System;
        if (http.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("Authorization must be assigned to each scoped request.", nameof(http));
    }

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public async Task ConfigureAsync(ServerScope scope, CancellationToken cancellationToken = default)
    {
        if (scopeOperation.Value?.IsActive == true)
            throw new InvalidOperationException("Não altere a conta ou o servidor durante uma operação do cliente.");
        using (await scopeGate.WriteAsync(cancellationToken))
        {
            await authenticationGate.WaitAsync(cancellationToken);
            try
            {
                session = null;
                Scope = scope;
                var saved = await sessions.GetAsync(scope, cancellationToken);
                if (saved is not null && saved.SessionExpiresAt > clock.GetUtcNow() && ValidSession(saved)) session = saved;
                else if (saved is not null) await sessions.ClearAsync(scope, cancellationToken);
            }
            finally { authenticationGate.Release(); }
        }
    }

    // A scope lease spans local I/O and all nested HTTP calls. Configuration cannot
    // change between checking private metadata and sending bytes or returning data.
    public async Task<T> ExecuteInScopeAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var previous = scopeOperation.Value;
        var lease = previous;
        if (lease is null || !lease.TryAddReference())
        {
            lease = new ScopeOperation(await scopeGate.ReadAsync(cancellationToken));
        }
        scopeOperation.Value = lease;
        try { return await operation(); }
        finally
        {
            scopeOperation.Value = previous;
            lease.Release();
        }
    }

    public Task ExecuteInScopeAsync(Func<Task> operation, CancellationToken cancellationToken = default) =>
        ExecuteInScopeAsync(async () => { await operation(); return true; }, cancellationToken);

    public Task LoginAsync(string password, string deviceName, string platform = "Android", CancellationToken cancellationToken = default) =>
        ExecuteInScopeAsync(() => LoginCoreAsync(password, deviceName, platform, cancellationToken), cancellationToken);

    private async Task LoginCoreAsync(string password, string deviceName, string platform, CancellationToken cancellationToken)
    {
        await authenticationGate.WaitAsync(cancellationToken);
        try
        {
            var scope = RequireScope();
            session = null;
            await sessions.ClearAsync(scope, cancellationToken);
            var device = await installation.GetDeviceIdAsync(scope, cancellationToken);
            using var request = JsonRequest(scope, HttpMethod.Post, "api/auth/login",
                new { login = scope.Login, password, deviceName, platform, deviceId = device });
            using var response = await SendRawAsync(request, cancellationToken);
            var tokens = await ReadJsonAsync<AuthenticationTokens>(response, cancellationToken);
            var next = SessionFrom(tokens);
            await installation.SetDeviceIdAsync(scope, next.DeviceId, cancellationToken);
            await sessions.SaveAsync(scope, next, cancellationToken);
            session = next;
        }
        finally { authenticationGate.Release(); }
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        ExecuteInScopeAsync(() => LogoutCoreAsync(cancellationToken), cancellationToken);

    private async Task LogoutCoreAsync(CancellationToken cancellationToken)
    {
        await authenticationGate.WaitAsync(cancellationToken);
        try
        {
            var scope = RequireScope();
            var previous = session;
            session = null;
            // Clear local credentials first, even when revocation cannot reach the server.
            await sessions.ClearAsync(scope, cancellationToken);
            if (previous is null) return;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(scope.Server, "api/auth/logout"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", previous.AccessToken);
            using var response = await SendRawAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized) await EnsureSuccessAsync(response, cancellationToken);
        }
        finally { authenticationGate.Release(); }
    }

    private async Task<(ServerScope Scope, SecureSession Session)> GetSessionAsync(CancellationToken cancellationToken)
    {
        await authenticationGate.WaitAsync(cancellationToken);
        try
        {
            var scope = RequireScope();
            var current = session ?? throw new LoginRequiredException();
            if (current.SessionExpiresAt <= clock.GetUtcNow())
            {
                session = null;
                await sessions.ClearAsync(scope, CancellationToken.None);
                throw new LoginRequiredException();
            }
            if (current.AccessExpiresAt > clock.GetUtcNow().AddSeconds(20)) return (scope, current);

            // A rotating refresh is single use. Remove its persisted copy BEFORE sending;
            // interruption, process death or a lost response must never replay it.
            session = null;
            await sessions.ClearAsync(scope, CancellationToken.None);
            try
            {
                using var request = JsonRequest(scope, HttpMethod.Post, "api/auth/refresh", new { refreshToken = current.RefreshToken });
                using var response = await SendRawAsync(request, cancellationToken);
                var next = SessionFrom(await ReadJsonAsync<AuthenticationTokens>(response, cancellationToken));
                if (next.SessionId != current.SessionId || next.DeviceId != current.DeviceId ||
                    next.SessionExpiresAt != current.SessionExpiresAt)
                    throw new InvalidDataException("Invalid refresh response.");
                await sessions.SaveAsync(scope, next, cancellationToken);
                session = next;
                return (scope, next);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException or NexoraApiException)
            {
                throw new LoginRequiredException();
            }
        }
        finally { authenticationGate.Release(); }
    }

    private SecureSession SessionFrom(AuthenticationTokens tokens)
    {
        if (tokens.TokenType != "Bearer" || tokens.ExpiresIn is <= 0 or > 3600 ||
            tokens.AccessToken.Length is < 1 or > 16384 || tokens.RefreshToken.Length is < 1 or > 128 ||
            tokens.DeviceId == Guid.Empty || tokens.SessionId == Guid.Empty || tokens.SessionExpiresAt <= clock.GetUtcNow())
            throw new InvalidDataException("Invalid authentication response.");
        return new SecureSession
        {
            AccessToken = tokens.AccessToken, RefreshToken = tokens.RefreshToken,
            AccessExpiresAt = clock.GetUtcNow().AddSeconds(tokens.ExpiresIn),
            SessionExpiresAt = tokens.SessionExpiresAt, DeviceId = tokens.DeviceId, SessionId = tokens.SessionId
        };
    }

    private static bool ValidSession(SecureSession value) =>
        !string.IsNullOrWhiteSpace(value.AccessToken) && !string.IsNullOrWhiteSpace(value.RefreshToken) &&
        value.DeviceId != Guid.Empty && value.SessionId != Guid.Empty;
    private ServerScope RequireScope() => Scope ?? throw new InvalidOperationException("Configure o servidor primeiro.");

    public Task<AssetPage> ListAssetsAsync(LibraryQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new LibraryQuery();
        if (query.Limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(query));
        var path = (query.Trash ? "api/trash" : "api/assets") + "?limit=" + query.Limit;
        if (query.Cursor is not null) path += "&cursor=" + Uri.EscapeDataString(query.Cursor);
        if (!query.Trash)
        {
            path += "&imagesOnly=" + (query.ImagesOnly ? "true" : "false");
            if (query.IsFavorite is not null) path += "&isFavorite=" + (query.IsFavorite.Value ? "true" : "false");
            if (query.Timeline) path += "&sort=timeline";
        }
        return GetAsync<AssetPage>(path, cancellationToken);
    }

    public Task<AssetSnapshot> GetAssetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        GetAsync<AssetSnapshot>($"api/assets/{assetId:D}", cancellationToken);
    public Task<AssetSnapshot> SetFavoriteAsync(Guid assetId, bool favorite, CancellationToken cancellationToken = default) =>
        JsonAuthorizedAsync<AssetSnapshot>(HttpMethod.Patch, $"api/assets/{assetId:D}", new { isFavorite = favorite }, cancellationToken);
    public Task<AssetSnapshot> RestoreAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        JsonAuthorizedAsync<AssetSnapshot>(HttpMethod.Post, $"api/trash/{assetId:D}/restore", null, cancellationToken);
    public Task MoveToTrashAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        ExecuteInScopeAsync(() => MoveToTrashCoreAsync(assetId, cancellationToken), cancellationToken);

    private async Task MoveToTrashCoreAsync(Guid assetId, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Delete, $"api/assets/{assetId:D}", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<SyncSnapshotPage> GetSyncSnapshotAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
        GetAsync<SyncSnapshotPage>("api/sync?limit=100" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), cancellationToken);
    public Task<SyncChangePage> GetChangesAsync(string cursor, CancellationToken cancellationToken = default) =>
        GetAsync<SyncChangePage>("api/sync/changes?limit=100&cursor=" + Uri.EscapeDataString(cursor), cancellationToken);
    public Task<UploadSnapshot> CreateUploadAsync(CreateUploadRequest request, CancellationToken cancellationToken = default) =>
        JsonAuthorizedAsync<UploadSnapshot>(HttpMethod.Post, "api/uploads", request, cancellationToken);
    public Task<UploadSnapshot> GetUploadAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<UploadSnapshot>($"api/uploads/{id:D}", cancellationToken);
    public Task<UploadSnapshot> CompleteUploadAsync(Guid id, CancellationToken cancellationToken = default) =>
        JsonAuthorizedAsync<UploadSnapshot>(HttpMethod.Post, $"api/uploads/{id:D}/complete", null, cancellationToken);
    public Task CancelUploadAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteInScopeAsync(() => CancelUploadCoreAsync(id, cancellationToken), cancellationToken);

    private async Task CancelUploadCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Delete, $"api/uploads/{id:D}", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }
    public Task<UploadChunkReceipt> PutChunkAsync(Guid id, int number, Stream content, long length, string hash, CancellationToken cancellationToken = default) =>
        ExecuteInScopeAsync(() => PutChunkCoreAsync(id, number, content, length, hash, cancellationToken), cancellationToken);

    private async Task<UploadChunkReceipt> PutChunkCoreAsync(Guid id, int number, Stream content, long length, string hash, CancellationToken cancellationToken)
    {
        using var body = new StreamContent(content, 64 * 1024);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentLength = length;
        using var response = await SendAuthorizedAsync(HttpMethod.Put, $"api/uploads/{id:D}/chunks/{number}", body, cancellationToken, hash);
        return await ReadJsonAsync<UploadChunkReceipt>(response, cancellationToken);
    }

    public Task DownloadAsync(Guid assetId, DownloadKind kind, Stream destination, CancellationToken cancellationToken = default,
        long maximumBytes = 2L * 1024 * 1024 * 1024) =>
        ExecuteInScopeAsync(() => DownloadCoreAsync(assetId, kind, destination, cancellationToken, maximumBytes), cancellationToken);

    private async Task DownloadCoreAsync(Guid assetId, DownloadKind kind, Stream destination, CancellationToken cancellationToken, long maximumBytes)
    {
        var route = kind switch { DownloadKind.Original => "content", DownloadKind.Thumbnail => "thumbnail", DownloadKind.Preview => "preview", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"api/assets/{assetId:D}/{route}", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (kind == DownloadKind.Original)
        {
            if (response.Content.Headers.ContentType?.MediaType != "application/octet-stream" ||
                response.Content.Headers.ContentDisposition?.DispositionType != "attachment")
                throw new InvalidDataException("O original deve ser entregue como anexo.");
        }
        else if (response.Content.Headers.ContentType?.MediaType != "image/png")
            throw new InvalidDataException("A prévia deve ser uma derivada PNG.");
        if (kind != DownloadKind.Original) maximumBytes = Math.Min(maximumBytes, 20 * 1024 * 1024);
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Download acima do limite.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await StreamLimits.CopyAsync(input, destination, maximumBytes, cancellationToken);
    }

    private Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        ExecuteInScopeAsync(() => GetCoreAsync<T>(path, cancellationToken), cancellationToken);

    private async Task<T> GetCoreAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, path, null, cancellationToken);
        return await ReadJsonAsync<T>(response, cancellationToken);
    }
    private Task<T> JsonAuthorizedAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        ExecuteInScopeAsync(() => JsonAuthorizedCoreAsync<T>(method, path, body, cancellationToken), cancellationToken);

    private async Task<T> JsonAuthorizedCoreAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var content = body is null ? null : JsonContent.Create(body, options: ProtocolJson.Options);
        using var response = await SendAuthorizedAsync(method, path, content, cancellationToken);
        return await ReadJsonAsync<T>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken cancellationToken, string? chunkHash = null)
    {
        var auth = await GetSessionAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, new Uri(auth.Scope.Server, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Session.AccessToken);
        if (chunkHash is not null) request.Headers.Add("X-Chunk-SHA256", chunkHash);
        var response = await SendRawAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await authenticationGate.WaitAsync(CancellationToken.None);
            try
            {
                if (Scope?.Key == auth.Scope.Key && ReferenceEquals(session, auth.Session))
                {
                    session = null;
                    await sessions.ClearAsync(auth.Scope, CancellationToken.None);
                }
            }
            finally { authenticationGate.Release(); }
            throw new LoginRequiredException();
        }
        if (Scope?.Key != auth.Scope.Key)
        {
            response.Dispose();
            throw new InvalidOperationException("A conta ou o servidor mudou durante a operação.");
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri)
        {
            response.Dispose();
            throw new InvalidDataException("Redirecionamento HTTP recusado.");
        }
        return response;
    }

    private static HttpRequestMessage JsonRequest(ServerScope scope, HttpMethod method, string path, object body) =>
        new(method, new Uri(scope.Server, path)) { Content = JsonContent.Create(body, options: ProtocolJson.Options) };
    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType is not "application/json") throw new InvalidDataException("Resposta JSON inválida.");
        var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, ProtocolJson.Options) ?? throw new InvalidDataException("Resposta vazia.");
    }
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var code = "http_" + (int)response.StatusCode;
        try
        {
            using var problem = JsonDocument.Parse(await ReadBoundedAsync(response.Content, cancellationToken));
            if (problem.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 and <= 64 } candidate && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                code = candidate;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { }
        throw new NexoraApiException(response.StatusCode, code);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumJsonBytes) throw new InvalidDataException("Resposta acima do limite.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var result = new MemoryStream();
        await StreamLimits.CopyAsync(stream, result, MaximumJsonBytes, cancellationToken);
        return result.ToArray();
    }

    private sealed class ScopeOperation(IDisposable admission)
    {
        private int references = 1;
        internal bool IsActive => Volatile.Read(ref references) > 0;
        internal bool TryAddReference()
        {
            int current;
            do
            {
                current = Volatile.Read(ref references);
                if (current == 0) return false;
            } while (Interlocked.CompareExchange(ref references, current + 1, current) != current);
            return true;
        }
        internal void Release()
        {
            if (Interlocked.Decrement(ref references) == 0) admission.Dispose();
        }
    }
}

internal static class StreamLimits
{
    internal static async Task<long> CopyAsync(Stream input, Stream output, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("Conteúdo acima do limite.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return total;
    }
}
