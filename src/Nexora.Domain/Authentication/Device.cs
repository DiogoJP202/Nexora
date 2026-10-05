namespace Nexora.Domain.Authentication;

public sealed class Device
{
    public const int MaximumNameLength = 100;
    public const int MaximumPlatformLength = 32;

    private Device() { }

    public Device(Guid id, Guid userId, string name, string platform, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        if (name.Length > MaximumNameLength || platform.Length > MaximumPlatformLength)
            throw new ArgumentException("Device metadata exceeds its maximum length.");

        Id = id;
        UserId = userId;
        Name = name;
        Platform = platform;
        CreatedAt = createdAt;
        LastSeenAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Platform { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public ICollection<AuthSession> Sessions { get; private set; } = [];

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
