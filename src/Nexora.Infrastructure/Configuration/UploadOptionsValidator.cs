using Microsoft.Extensions.Options;

namespace Nexora.Infrastructure.Configuration;

public sealed class UploadOptionsValidator : IValidateOptions<UploadOptions>
{
    public ValidateOptionsResult Validate(string? name, UploadOptions options)
    {
        if (options.ChunkSizeBytes is < 1 or > 64 * 1024 * 1024
            || options.MaximumFileSizeBytes < 0 || options.MaximumFileSizeBytes > long.MaxValue / 2
            || options.MaximumReservedBytes <= 0 || options.MinimumFreeBytes < 0
            || options.MaximumOpenUploadsPerOwner is < 1 or > 100 || options.MaximumJobAttempts is < 1 or > 100)
            return ValidateOptionsResult.Fail("Upload size, capacity and concurrency limits must be valid.");

        var chunkCount = options.MaximumFileSizeBytes / options.ChunkSizeBytes
            + (options.MaximumFileSizeBytes % options.ChunkSizeBytes == 0 ? 0 : 1);
        if (chunkCount > 65_536)
            return ValidateOptionsResult.Fail("The configured file and chunk sizes allow too many chunks.");

        var durations = new[] { options.InactivityExpiration, options.ChunkTimeout, options.LeaseDuration,
            options.LeaseRenewInterval, options.ProcessingTimeout, options.RetryDelay, options.PollInterval,
            options.MaintenanceInterval, options.OrphanGracePeriod };
        if (durations.Any(duration => duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(365)))
            return ValidateOptionsResult.Fail("Upload and worker durations must be positive and at most 365 days.");
        var timers = new[] { options.ChunkTimeout, options.LeaseDuration, options.LeaseRenewInterval,
            options.ProcessingTimeout, options.PollInterval, options.MaintenanceInterval };
        if (timers.Any(duration => duration > TimeSpan.FromDays(30)))
            return ValidateOptionsResult.Fail("Process timers must not exceed 30 days.");
        if (options.LeaseRenewInterval >= options.LeaseDuration / 2
            || options.OrphanGracePeriod <= options.ProcessingTimeout + options.ChunkTimeout + options.LeaseDuration)
            return ValidateOptionsResult.Fail("Lease renewal and orphan grace must cover the processing intervals.");
        return ValidateOptionsResult.Success;
    }
}
