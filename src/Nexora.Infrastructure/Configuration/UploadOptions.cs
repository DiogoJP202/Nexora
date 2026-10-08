namespace Nexora.Infrastructure.Configuration;

public sealed class UploadOptions
{
    public const string SectionName = "Uploads";
    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;
    public long MaximumFileSizeBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public int MaximumOpenUploadsPerOwner { get; set; } = 2;
    public long MaximumReservedBytes { get; set; } = 50L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 50L * 1024 * 1024 * 1024;
    public TimeSpan InactivityExpiration { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan ChunkTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromHours(2);
    public int MaximumJobAttempts { get; set; } = 5;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan OrphanGracePeriod { get; set; } = TimeSpan.FromDays(1);
}
