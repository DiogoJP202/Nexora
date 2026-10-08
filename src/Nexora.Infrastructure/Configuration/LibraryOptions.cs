namespace Nexora.Infrastructure.Configuration;

public sealed class LibraryOptions
{
    public const string SectionName = "Library";
    public TimeSpan TrashRetention { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan UnreferencedBlobGracePeriod { get; set; } = TimeSpan.FromDays(1);
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(1);
    public int MaintenanceBatchSize { get; set; } = 100;
}
