using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Nexora.IntegrationTests;

internal sealed class DataProtectionTestFiles : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-protection-it_" + Guid.NewGuid().ToString("N"));

    public DataProtectionTestFiles()
    {
        Directory.CreateDirectory(_root);
        KeyDirectory = Path.Combine(_root, "keys");
        CertificatePath = Path.Combine(_root, "protection.pfx");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Nexora integration tests", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        File.WriteAllBytes(CertificatePath, certificate.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(CertificatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public string KeyDirectory { get; }
    public string CertificatePath { get; }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(_root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullPath)!);
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(parent, expectedParent, comparison) ||
            !Regex.IsMatch(Path.GetFileName(fullPath), "^nexora-protection-it_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Recusada limpeza de diretório que não pertence ao fixture de proteção.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
