namespace Nexora.Application.Authentication;

public static class AuthenticationConstants
{
    public const string Scheme = "NexoraBearer";
    public const string AdministratorRole = "Administrator";
    public const string SessionIdClaim = "nexora_session_id";
    public const string DeviceIdClaim = "nexora_device_id";
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
}
