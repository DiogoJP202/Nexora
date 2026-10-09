using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexora.Mobile.Core;

public sealed record ServerScope
{
    private ServerScope(Uri server, string login)
    {
        Server = server;
        Login = login;
        Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.AbsoluteUri + "\n" + login.ToUpperInvariant()))).ToLowerInvariant();
    }

    public Uri Server { get; }
    public string Login { get; }
    public string Key { get; }

    public static ServerScope Create(string serverUrl, string login, bool allowLocalDevelopmentHttp = false)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var server) ||
            !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) ||
            !string.IsNullOrEmpty(server.Fragment) || server.AbsolutePath != "/")
            throw new ArgumentException("Informe a URL raiz do servidor, sem credenciais, caminho ou parâmetros.");
        var local = server.Host is "localhost" or "127.0.0.1" or "::1" or "10.0.2.2";
        if (server.Scheme != Uri.UriSchemeHttps && !(allowLocalDevelopmentHttp && local && server.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("O servidor exige HTTPS. HTTP de desenvolvimento aceita somente o emulador ou loopback.");
        login = login.Trim();
        if (string.IsNullOrWhiteSpace(login) || login.Length > 254 || login.Any(char.IsControl))
            throw new ArgumentException("Informe a conta.");
        return new ServerScope(new Uri(server.GetLeftPart(UriPartial.Authority) + "/"), login);
    }
}

// Implementations MUST use the platform keystore; this interface deliberately has no file fallback.
public interface ISecureSessionStore
{
    Task<SecureSession?> GetAsync(ServerScope scope, CancellationToken cancellationToken = default);
    Task SaveAsync(ServerScope scope, SecureSession session, CancellationToken cancellationToken = default);
    Task ClearAsync(ServerScope scope, CancellationToken cancellationToken = default);
}

public interface IInstallationIdentity
{
    Task<Guid?> GetDeviceIdAsync(ServerScope scope, CancellationToken cancellationToken = default);
    Task SetDeviceIdAsync(ServerScope scope, Guid deviceId, CancellationToken cancellationToken = default);
}

public sealed class SecureSession
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public DateTimeOffset AccessExpiresAt { get; init; }
    public DateTimeOffset SessionExpiresAt { get; init; }
    public Guid DeviceId { get; init; }
    public Guid SessionId { get; init; }
    public override string ToString() => "SecureSession [redacted]";
}

internal sealed class AuthenticationTokens
{
    public required string TokenType { get; init; }
    public required string AccessToken { get; init; }
    public long ExpiresIn { get; init; }
    public required string RefreshToken { get; init; }
    public Guid DeviceId { get; init; }
    public Guid SessionId { get; init; }
    public DateTimeOffset SessionExpiresAt { get; init; }
    public override string ToString() => "AuthenticationTokens [redacted]";
}

// The API's default System.Text.Json contract encodes these enums as numbers.
public enum ImageProcessingState { Pending = 0, Processing = 1, Ready = 2, Failed = 3 }
public enum UploadState { Open = 0, Finalizing = 1, Completed = 2, Cancelled = 3, Expired = 4, Failed = 5 }
public enum BackgroundJobState { Pending = 0, Running = 1, Succeeded = 2, Failed = 3, Cancelled = 4 }
public enum DownloadKind { Original, Thumbnail, Preview }
public sealed record ImageSnapshot(ImageProcessingState State, int? Width, int? Height, DateTime? CapturedAtLocal,
    DateTimeOffset? CapturedAtUtc, DateTimeOffset? ProcessedAt, bool HasThumbnail, bool HasPreview, string? FailureCode);
public sealed record AssetSnapshot(Guid Id, string OriginalName, long Size, string DetectedMimeType,
    DateTimeOffset UploadedAt, bool IsFavorite, DateTimeOffset? DeletedAt, ImageSnapshot? Image = null);
public sealed record AssetPage(IReadOnlyList<AssetSnapshot> Items, string? NextCursor);
public sealed record LibraryQuery(bool Trash = false, bool? IsFavorite = null, bool ImagesOnly = false,
    bool Timeline = false, int Limit = 50, string? Cursor = null);
public sealed record SyncSnapshotPage(IReadOnlyList<AssetSnapshot> Items, string? NextCursor, string Cursor);
public sealed record SyncChange(long Sequence, string Kind, Guid AssetId, AssetSnapshot? Asset);
public sealed record SyncChangePage(IReadOnlyList<SyncChange> Items, string Cursor, bool HasMore);
public sealed record CreateUploadRequest(string OriginalName, long ExpectedLength, string? ExpectedSha256, Guid? ClientRequestId = null);
public sealed record UploadOperationSummary(Guid Id, BackgroundJobState State, int Attempts, string? FailureCode);
public sealed record UploadSnapshot(Guid Id, string OriginalName, long ExpectedLength, int ChunkSize,
    int ChunkCount, UploadState State, int[] ConfirmedChunks, DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt, AssetSnapshot? Result, string? FailureCode, UploadOperationSummary? Operation,
    DateTimeOffset? ResultPurgedAt = null);
public sealed record UploadChunkReceipt(int Number, long Size, string Sha256, bool Reused);

public sealed class LoginRequiredException : Exception
{
    public LoginRequiredException() : base("A sessão expirou ou foi revogada. Entre novamente.") { }
}

public sealed class NexoraApiException(HttpStatusCode statusCode, string code) : Exception(MessageFor(code))
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    private static string MessageFor(string code) => code switch
    {
        "invalid_credentials" => "Credenciais inválidas.",
        "invalid_device" => "O dispositivo foi revogado. Remova a identificação salva para cadastrar outro dispositivo.",
        "rate_limited" => "Limite de requisições excedido. Tente novamente mais tarde.",
        "storage_capacity_exceeded" or "insufficient_storage" => "Espaço insuficiente no servidor.",
        "resource_not_found" => "Recurso não encontrado.",
        _ => "O servidor não concluiu a operação (" + code + ")."
    };
}

internal static class ProtocolJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false
    };
}
