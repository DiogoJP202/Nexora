namespace Nexora.Domain.Authentication;

public sealed class RefreshToken
{
    private RefreshToken() { }

    public RefreshToken(Guid id, Guid sessionId, byte[] tokenHash,
        DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        if (tokenHash.Length != 32) throw new ArgumentException("Refresh token hashes must contain 32 bytes.");
        if (expiresAt <= createdAt) throw new ArgumentException("Refresh expiration must follow its creation.");

        Id = id;
        SessionId = sessionId;
        TokenHash = tokenHash.ToArray();
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public byte[] TokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public Guid? SuccessorId { get; private set; }
    public AuthSession Session { get; private set; } = null!;
    public RefreshToken? Successor { get; private set; }

    public void Consume(DateTimeOffset now, Guid successorId)
    {
        if (UsedAt is not null) throw new InvalidOperationException("A refresh token can only be consumed once.");
        UsedAt = now;
        SuccessorId = successorId;
    }
}
