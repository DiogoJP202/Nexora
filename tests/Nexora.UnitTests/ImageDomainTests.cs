using Nexora.Application.Images;
using Nexora.Domain.Images;

namespace Nexora.UnitTests;

public sealed class ImageDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void Capture_without_timezone_is_preserved_as_local_and_is_not_assigned_a_utc_value()
    {
        var image = NewImage();
        var captured = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);
        image.BeginProcessing(100);
        image.Complete(Guid.NewGuid(), 1200, 800, captured, null, 40, Hash, 60, Hash, Now);
        Assert.Equal(captured, image.CapturedAtLocal);
        Assert.Equal(DateTimeKind.Unspecified, image.CapturedAtLocal!.Value.Kind);
        Assert.Null(image.CapturedAtUtc);
        Assert.Equal(ImageProcessingState.Ready, image.State);
        Assert.Equal(100, image.ReservedBytes);
        image.FinishCleanup();
        Assert.Equal(0, image.ReservedBytes);
        Assert.NotNull(image.DerivativeGenerationId);
    }

    [Fact]
    public void Rejected_capture_timestamps_do_not_publish_a_partial_metadata_result()
    {
        var image = NewImage();
        image.BeginProcessing(100);
        Assert.Throws<ArgumentException>(() => image.Complete(Guid.NewGuid(), 10, 10,
            new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc), null, 10, Hash, 20, Hash, Now));
        Assert.Throws<ArgumentException>(() => image.Complete(Guid.NewGuid(), 10, 10, null,
            Now.ToOffset(TimeSpan.FromHours(2)), 10, Hash, 20, Hash, Now));
        Assert.Equal(ImageProcessingState.Processing, image.State);
        Assert.Null(image.Width);
        Assert.Null(image.CapturedAtLocal);
        Assert.Null(image.DerivativeGenerationId);
    }

    [Fact]
    public void Reservation_covers_both_derivatives_and_survives_a_retry_until_terminal_cleanup()
    {
        var image = NewImage();
        image.BeginProcessing(100);
        Assert.Throws<InvalidOperationException>(() => image.Complete(Guid.NewGuid(), 10, 10,
            null, null, 60, Hash, 41, Hash, Now));
        Assert.Null(image.ThumbnailLength);
        image.Fail("storage_unavailable", true);
        Assert.Equal(ImageProcessingState.Pending, image.State);
        Assert.Throws<InvalidOperationException>(image.FinishCleanup);
        Assert.Throws<InvalidOperationException>(() => image.BeginProcessing(99));
        image.BeginProcessing(100);
        image.Fail("invalid_image", false);
        Assert.Equal(ImageProcessingState.Failed, image.State);
        Assert.Equal(100, image.ReservedBytes);
        image.FinishCleanup();
        image.FinishCleanup();
        Assert.Equal(0, image.ReservedBytes);
        Assert.Throws<InvalidOperationException>(() => image.BeginProcessing(100));
    }

    [Fact]
    public void Invalid_derivative_digest_does_not_turn_processing_image_into_ready()
    {
        var image = NewImage();
        image.BeginProcessing(100);
        Assert.Throws<ArgumentException>(() => image.Complete(Guid.NewGuid(), 10, 10,
            null, null, 10, Hash.ToUpperInvariant(), 20, Hash, Now));
        Assert.Equal(ImageProcessingState.Processing, image.State);
        Assert.Null(image.ProcessedAt);
        Assert.Null(image.PreviewLength);
    }

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/heic", false)]
    [InlineData("video/mp4", false)]
    [InlineData("application/octet-stream", false)]
    public void Only_initially_supported_formats_enter_image_processing(string mimeType, bool expected) =>
        Assert.Equal(expected, BlobImage.Supports(mimeType));

    [Fact]
    public void Derivative_keys_use_a_physical_generation_and_validate_default_or_invalid_values()
    {
        var generation = Guid.Parse("a8526811-039a-414e-92aa-164d3b915085");
        Assert.Equal("thumbnails/a8/52/a8526811039a414e92aa164d3b915085.png",
            new DerivativeKey(generation, DerivativeKind.Thumbnail).ToString());
        Assert.Equal("previews/a8/52/a8526811039a414e92aa164d3b915085.png",
            new DerivativeKey(generation, DerivativeKind.Preview).ToString());
        Assert.Throws<ArgumentException>(() => new DerivativeKey(Guid.Empty, DerivativeKind.Thumbnail));
        Assert.Throws<ArgumentException>(() => new DerivativeKey(generation, (DerivativeKind)123));
        Assert.Throws<ArgumentException>(() => default(DerivativeKey).ToString());
    }

    private static BlobImage NewImage() => new(Guid.NewGuid(), Now);
}
