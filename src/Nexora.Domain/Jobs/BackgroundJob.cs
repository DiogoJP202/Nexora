using Nexora.Domain.Uploads;

namespace Nexora.Domain.Jobs;

public sealed class BackgroundJob
{
    private BackgroundJob() { }

    public BackgroundJob(Guid id, Guid uploadSessionId, DateTimeOffset createdAt, int maximumAttempts)
    {
        if (id == Guid.Empty || uploadSessionId == Guid.Empty) throw new ArgumentException("A job requires valid identifiers.");
        if (createdAt.Offset != TimeSpan.Zero) throw new ArgumentException("The timestamp must be UTC.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        if (maximumAttempts > 100) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        Id = id;
        UploadSessionId = uploadSessionId;
        CreatedAt = NextAttemptAt = createdAt;
        MaximumAttempts = maximumAttempts;
    }

    public Guid Id { get; private set; }
    public Guid UploadSessionId { get; private set; }
    public string Kind { get; private set; } = "FinalizeUpload";
    public BackgroundJobState State { get; private set; } = BackgroundJobState.Pending;
    public int Attempts { get; private set; }
    public int MaximumAttempts { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public string? FailureCode { get; private set; }
    public UploadSession Upload { get; private set; } = null!;

    public bool HasLease(Guid token, DateTimeOffset now)
        => State == BackgroundJobState.Running && LeaseToken == token && LeaseExpiresAt > now;

    public void Claim(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        if (token == Guid.Empty || now.Offset != TimeSpan.Zero || duration <= TimeSpan.Zero || Attempts >= MaximumAttempts
            || !((State == BackgroundJobState.Pending && NextAttemptAt <= now)
                || (State == BackgroundJobState.Running && LeaseExpiresAt <= now)))
            throw new InvalidOperationException("The job is not eligible for a lease.");
        var expiration = now + duration;
        State = BackgroundJobState.Running;
        LeaseToken = token;
        LeaseExpiresAt = expiration;
        Attempts++;
    }

    public void Renew(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        if (!HasLease(token, now) || now.Offset != TimeSpan.Zero || duration <= TimeSpan.Zero)
            throw new InvalidOperationException("The lease is not valid.");
        var extended = now + duration;
        if (extended > LeaseExpiresAt) LeaseExpiresAt = extended;
    }

    public void Succeed()
    {
        if (State != BackgroundJobState.Running) throw new InvalidOperationException("Only a running job can succeed.");
        State = BackgroundJobState.Succeeded;
        FailureCode = null;
        LeaseToken = null;
        LeaseExpiresAt = null;
    }

    public void Fail(string code, bool retryable, DateTimeOffset now, TimeSpan delay)
    {
        if (State is not (BackgroundJobState.Pending or BackgroundJobState.Running))
            throw new InvalidOperationException("Only an active job can fail.");
        UploadSession.ValidateFailureCode(code);
        if (now.Offset != TimeSpan.Zero || delay < TimeSpan.Zero) throw new ArgumentException("The retry time is invalid.");
        var nextAttempt = now + delay;
        FailureCode = code;
        LeaseToken = null;
        LeaseExpiresAt = null;
        State = retryable && Attempts < MaximumAttempts ? BackgroundJobState.Pending : BackgroundJobState.Failed;
        NextAttemptAt = nextAttempt;
    }

    public void Cancel()
    {
        if (State is BackgroundJobState.Succeeded or BackgroundJobState.Failed) return;
        State = BackgroundJobState.Cancelled;
        LeaseToken = null;
        // An invalidated worker may still own open streams until its old lease expires.
    }
}
