using Microsoft.Extensions.Options;

namespace Nexora.Infrastructure.Configuration;

public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            return ValidateOptionsResult.Fail("Storage:RootPath is required.");
        }

        if (!Path.IsPathFullyQualified(options.RootPath))
        {
            return ValidateOptionsResult.Fail("Storage:RootPath must be an absolute path for the current operating system.");
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
            var rootPath = Path.GetPathRoot(fullPath);

            if (string.IsNullOrEmpty(rootPath) ||
                string.Equals(fullPath, Path.TrimEndingDirectorySeparator(rootPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return ValidateOptionsResult.Fail("Storage:RootPath must not be a filesystem root.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ValidateOptionsResult.Fail("Storage:RootPath must be a valid absolute directory path.");
        }

        return ValidateOptionsResult.Success;
    }
}
