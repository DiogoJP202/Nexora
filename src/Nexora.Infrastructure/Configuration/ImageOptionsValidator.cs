using Microsoft.Extensions.Options;

namespace Nexora.Infrastructure.Configuration;

public sealed class ImageOptionsValidator : IValidateOptions<ImageOptions>
{
    public ValidateOptionsResult Validate(string? name, ImageOptions options)
    {
        if (options.MaximumInputBytes is < 1 or > 256L * 1024 * 1024
            || options.MaximumPixels is < 1 or > 100_000_000
            || options.MaximumDecodedBytes is < 4 or > 512L * 1024 * 1024
            || options.MaximumDimension is < 1 or > 32_768
            || options.ThumbnailSize is < 1 or > 1_024
            || options.PreviewSize < options.ThumbnailSize || options.PreviewSize > 4_096
            || options.MaximumDerivativeBytes is < 1 or > 32 * 1024 * 1024
            || options.MaximumProcessMemoryBytes is < 64L * 1024 * 1024 or > 2L * 1024 * 1024 * 1024
            || options.MaximumJobAttempts is < 1 or > 100)
            return ValidateOptionsResult.Fail("Image size, memory and attempt limits must be valid.");

        // Leave room for native codec state, metadata, two encoded results and the runtime.
        var requiredMemory = options.MaximumDecodedBytes + 2 * options.MaximumInputBytes
            + 4L * options.MaximumDerivativeBytes + 64L * 1024 * 1024;
        if (options.MaximumProcessMemoryBytes < requiredMemory)
            return ValidateOptionsResult.Fail("The image process memory limit must cover the configured buffers and runtime.");

        var durations = new[] { options.ProcessingTimeout, options.LeaseDuration, options.LeaseRenewInterval,
            options.RetryDelay, options.PollInterval, options.MaintenanceInterval };
        if (durations.Any(duration => duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(30)))
            return ValidateOptionsResult.Fail("Image process timers must be positive and at most 30 days.");
        if (options.LeaseRenewInterval >= options.LeaseDuration / 2)
            return ValidateOptionsResult.Fail("Image leases must renew before half their duration.");
        return ValidateOptionsResult.Success;
    }
}
