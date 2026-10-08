namespace Nexora.Domain.Jobs;

public sealed class BackgroundJobAttempt
{
    private BackgroundJobAttempt() { }

    public BackgroundJobAttempt(Guid id, Guid jobId, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || jobId == Guid.Empty) throw new ArgumentException("A job attempt requires valid identifiers.");
        if (createdAt.Offset != TimeSpan.Zero) throw new ArgumentException("The timestamp must be UTC.");
        Id = id;
        JobId = jobId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public BackgroundJob Job { get; private set; } = null!;
}
