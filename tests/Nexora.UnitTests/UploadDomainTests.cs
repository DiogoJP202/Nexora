using Nexora.Domain.Jobs;
using Nexora.Domain.Uploads;

namespace Nexora.UnitTests;

public sealed class UploadDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(8, 1, 8)]
    [InlineData(17, 3, 1)]
    public void Chunk_layout_and_peak_reservation_use_actual_length(long length, int count, long lastLength)
    {
        var upload = NewUpload(length);
        Assert.Equal(count, upload.ChunkCount);
        Assert.Equal(length * 2, upload.ReservedBytes);
        if (count != 0) Assert.Equal(lastLength, upload.ExpectedChunkLength(count - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => upload.ExpectedChunkLength(count));
        Assert.Throws<ArgumentOutOfRangeException>(() => upload.ExpectedChunkLength(-1));
    }

    [Fact]
    public void Reservation_survives_cancellation_until_physical_cleanup()
    {
        var upload = NewUpload(8);
        Assert.Throws<InvalidOperationException>(upload.FinishCleanup);
        upload.Cancel(Now);
        upload.Cancel(Now);
        Assert.Equal(16, upload.ReservedBytes);
        upload.FinishCleanup();
        Assert.Equal(0, upload.ReservedBytes);
    }

    [Fact]
    public void Recorded_assembly_cannot_be_replaced_and_finished_upload_cannot_reopen()
    {
        var upload = NewUpload(8);
        var key = Guid.NewGuid();
        var hash = new string('a', 64);
        upload.BeginFinalization(Now);
        upload.SaveAssembly(key, 8, hash, "application/octet-stream", Now);
        upload.SaveAssembly(key, 8, hash, "application/octet-stream", Now);
        Assert.Throws<InvalidOperationException>(() => upload.SaveAssembly(Guid.NewGuid(), 8, hash,
            "application/octet-stream", Now));
        upload.Complete(Guid.NewGuid(), Now);
        Assert.Equal(UploadState.Completed, upload.State);
        Assert.Throws<InvalidOperationException>(() => upload.BeginFinalization(Now));
        Assert.Throws<InvalidOperationException>(() => upload.Cancel(Now));
        upload.FinishCleanup();
        Assert.Null(upload.AssemblyTemporaryId);
    }

    [Fact]
    public void Expired_lease_is_reclaimed_and_old_token_cannot_renew()
    {
        var job = NewJob(3);
        var first = Guid.NewGuid();
        job.Claim(first, Now, TimeSpan.FromMinutes(1));
        Assert.True(job.HasLease(first, Now.AddSeconds(59)));
        Assert.False(job.HasLease(first, Now.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => job.Claim(Guid.NewGuid(), Now.AddSeconds(59), TimeSpan.FromMinutes(1)));
        var second = Guid.NewGuid();
        job.Claim(second, Now.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.Equal(2, job.Attempts);
        Assert.Throws<InvalidOperationException>(() => job.Renew(first, Now.AddMinutes(1), TimeSpan.FromMinutes(1)));
        Assert.True(job.HasLease(second, Now.AddMinutes(1)));
    }

    [Fact]
    public void Retry_waits_for_schedule_and_stops_at_attempt_budget()
    {
        var job = NewJob(2);
        job.Claim(Guid.NewGuid(), Now, TimeSpan.FromMinutes(1));
        job.Fail("storage_unavailable", true, Now, TimeSpan.FromSeconds(30));
        Assert.Equal(BackgroundJobState.Pending, job.State);
        Assert.Throws<InvalidOperationException>(() => job.Claim(Guid.NewGuid(), Now.AddSeconds(29), TimeSpan.FromMinutes(1)));
        job.Claim(Guid.NewGuid(), Now.AddSeconds(30), TimeSpan.FromMinutes(1));
        job.Fail("storage_unavailable", true, Now.AddSeconds(30), TimeSpan.Zero);
        Assert.Equal(BackgroundJobState.Failed, job.State);
        Assert.Equal("storage_unavailable", job.FailureCode);
        Assert.Throws<InvalidOperationException>(() => job.Claim(Guid.NewGuid(), Now.AddMinutes(2), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Cancellation_invalidates_token_but_preserves_old_worker_deadline()
    {
        var job = NewJob(2);
        var token = Guid.NewGuid();
        job.Claim(token, Now, TimeSpan.FromMinutes(1));
        job.Cancel();
        Assert.False(job.HasLease(token, Now));
        Assert.Equal(Now.AddMinutes(1), job.LeaseExpiresAt);
        Assert.Equal(BackgroundJobState.Cancelled, job.State);
    }

    private static UploadSession NewUpload(long length) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "original.bin", length, null, 8, Now);
    private static BackgroundJob NewJob(int attempts) => new(Guid.NewGuid(), Guid.NewGuid(), Now, attempts);
}
