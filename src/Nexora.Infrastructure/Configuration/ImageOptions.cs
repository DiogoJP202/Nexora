namespace Nexora.Infrastructure.Configuration;

public sealed class ImageOptions
{
    public const string SectionName = "Images";
    public long MaximumInputBytes { get; set; } = 32L * 1024 * 1024;
    public long MaximumPixels { get; set; } = 24_000_000;
    public long MaximumDecodedBytes { get; set; } = 128L * 1024 * 1024;
    public int MaximumDimension { get; set; } = 16_384;
    public int ThumbnailSize { get; set; } = 256;
    public int PreviewSize { get; set; } = 1_280;
    public int MaximumDerivativeBytes { get; set; } = 8 * 1024 * 1024;
    public long MaximumProcessMemoryBytes { get; set; } = 512L * 1024 * 1024;
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(20);
    public int MaximumJobAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(1);
}
