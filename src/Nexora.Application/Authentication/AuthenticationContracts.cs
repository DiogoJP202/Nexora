namespace Nexora.Application.Authentication;

public sealed record LoginCommand(string Login, string Password, string DeviceName, string Platform, Guid? DeviceId = null);

public sealed record AuthenticationTokens(
    string TokenType,
    string AccessToken,
    long ExpiresIn,
    string RefreshToken,
    Guid DeviceId,
    Guid SessionId,
    DateTimeOffset SessionExpiresAt);

public sealed record DeviceSummary(Guid Id, string Name, string Platform,
    DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt);

public enum AuthenticationFailure
{
    InvalidCredentials,
    InvalidRefreshToken,
    InvalidDevice,
    AlreadyInitialized,
    PasswordRejected,
    AdministratorNotFound
}

public sealed record AuthenticationResult<T>(T? Value, AuthenticationFailure? Failure)
{
    public bool Succeeded => Failure is null;
    public static AuthenticationResult<T> Success(T value) => new(value, null);
    public static AuthenticationResult<T> Fail(AuthenticationFailure failure) => new(default, failure);
}

public interface IAuthenticationService
{
    Task<AuthenticationResult<AuthenticationTokens>> LoginAsync(LoginCommand command, CancellationToken cancellationToken);
    Task<AuthenticationResult<AuthenticationTokens>> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task LogoutAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceSummary>> ListDevicesAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
    Task<bool> ValidateSessionAsync(Guid userId, Guid sessionId, Guid deviceId, string securityStamp, CancellationToken cancellationToken);
}

public interface IAccountAdministration
{
    Task<AuthenticationResult<Guid>> BootstrapAsync(string email, string password, CancellationToken cancellationToken);
    Task<AuthenticationResult<Guid>> ResetAdministratorPasswordAsync(string password, CancellationToken cancellationToken);
}
