using Microsoft.Extensions.Options;

namespace Nexora.Api.Security;

public sealed class KeyProtectionOptions
{
    public const string SectionName = "DataProtection";
    public string KeyDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexora", "keys");
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
}

internal sealed class KeyProtectionOptionsValidator(IHostEnvironment environment) : IValidateOptions<KeyProtectionOptions>
{
    public ValidateOptionsResult Validate(string? name, KeyProtectionOptions options)
    {
        if (!AbsoluteDirectory(options.KeyDirectory))
        {
            return ValidateOptionsResult.Fail("DataProtection:KeyDirectory deve ser um diretório absoluto fora da raiz do volume.");
        }
        if (!string.IsNullOrWhiteSpace(options.CertificatePath) && !Path.IsPathFullyQualified(options.CertificatePath))
        {
            return ValidateOptionsResult.Fail("DataProtection:CertificatePath deve ser absoluto.");
        }
        if (!OperatingSystem.IsWindows() && !environment.IsDevelopment() && string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            return ValidateOptionsResult.Fail("DataProtection:CertificatePath é obrigatório fora de Development no Linux.");
        }
        return ValidateOptionsResult.Success;
    }

    private static bool AbsoluteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }
        try
        {
            return !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
