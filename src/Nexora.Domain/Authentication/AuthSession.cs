namespace Nexora.Domain.Authentication;

public sealed class AuthSession
{
    private AuthSession() { }

    public AuthSession(Guid id, Guid userId, Guid deviceId, string securityStamp,
        DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        if (expiresAt <= createdAt) throw new ArgumentException("Session expiration must follow its creation.");

        Id = id;
        UserId = userId;
        DeviceId = deviceId;
        SecurityStamp = securityStamp;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string SecurityStamp { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Device Device { get; private set; } = null!;
    public ICollection<RefreshToken> RefreshTokens { get; private set; } = [];

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
