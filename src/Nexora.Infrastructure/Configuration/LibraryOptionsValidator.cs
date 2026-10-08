using Microsoft.Extensions.Options;

namespace Nexora.Infrastructure.Configuration;

public sealed class LibraryOptionsValidator : IValidateOptions<LibraryOptions>
{
    public ValidateOptionsResult Validate(string? name, LibraryOptions options)
    {
        var errors = new List<string>();
        if (options.TrashRetention <= TimeSpan.Zero || options.TrashRetention > TimeSpan.FromDays(365))
            errors.Add("Library:TrashRetention must be positive and at most 365 days.");
        if (options.UnreferencedBlobGracePeriod <= TimeSpan.Zero || options.UnreferencedBlobGracePeriod > TimeSpan.FromDays(365))
            errors.Add("Library:UnreferencedBlobGracePeriod must be positive and at most 365 days.");
        if (options.MaintenanceInterval <= TimeSpan.Zero || options.MaintenanceInterval > TimeSpan.FromDays(30))
            errors.Add("Library:MaintenanceInterval must be positive and at most 30 days.");
        if (options.MaintenanceBatchSize is < 1 or > 1000)
            errors.Add("Library:MaintenanceBatchSize must be between 1 and 1000.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
