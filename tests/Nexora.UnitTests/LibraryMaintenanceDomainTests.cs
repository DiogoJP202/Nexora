using Nexora.Domain.Uploads;

namespace Nexora.UnitTests;

public sealed class LibraryMaintenanceDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Purged_result_preserves_success_and_first_purge_timestamp()
    {
        var upload = NewUpload();
        upload.BeginFinalization(Now);
        upload.Complete(Guid.NewGuid(), Now);
        var purgedAt = Now.AddDays(31);

        upload.MarkResultPurged(purgedAt);
        upload.MarkResultPurged(purgedAt.AddDays(1));

        Assert.Equal(UploadState.Completed, upload.State);
        Assert.Null(upload.ResultAssetId);
        Assert.Null(upload.FailureCode);
        Assert.Equal(purgedAt, upload.ResultPurgedAt);
        Assert.Throws<InvalidOperationException>(() => upload.BeginFinalization(purgedAt));
        Assert.Throws<InvalidOperationException>(() => upload.Complete(Guid.NewGuid(), purgedAt));
    }

    [Fact]
    public void Active_upload_cannot_be_marked_as_having_a_purged_result()
    {
        var upload = NewUpload();
        Assert.Throws<InvalidOperationException>(() => upload.MarkResultPurged(Now));
        upload.BeginFinalization(Now);
        Assert.Throws<InvalidOperationException>(() => upload.MarkResultPurged(Now));
        Assert.Equal(UploadState.Finalizing, upload.State);
        Assert.Null(upload.ResultPurgedAt);
    }

    [Fact]
    public void Failed_upload_cannot_be_marked_as_successfully_purged()
    {
        var upload = NewUpload();
        upload.Fail("asset_in_trash", Now);

        Assert.Throws<InvalidOperationException>(() => upload.MarkResultPurged(Now.AddDays(31)));
        Assert.Equal("asset_in_trash", upload.FailureCode);
        Assert.Equal(UploadState.Failed, upload.State);
    }

    [Fact]
    public void Purge_timestamp_requires_utc()
    {
        var upload = NewUpload();
        upload.BeginFinalization(Now);
        var asset = Guid.NewGuid();
        upload.Complete(asset, Now);

        Assert.Throws<ArgumentException>(() => upload.MarkResultPurged(Now.ToOffset(TimeSpan.FromHours(1))));
        Assert.Equal(asset, upload.ResultAssetId);
        Assert.Null(upload.ResultPurgedAt);
    }

    private static UploadSession NewUpload() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "original.bin", 8, null, 8, Now);
}
