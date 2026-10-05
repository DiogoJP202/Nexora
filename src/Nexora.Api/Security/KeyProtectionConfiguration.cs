using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Nexora.Api.Security;

internal static class KeyProtectionConfiguration
{
    internal static IServiceCollection AddKeyProtection(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<IValidateOptions<KeyProtectionOptions>, KeyProtectionOptionsValidator>();
        services.AddOptions<KeyProtectionOptions>().Bind(configuration.GetSection(KeyProtectionOptions.SectionName))
            .ValidateOnStart();
        services.AddDataProtection().SetApplicationName("Nexora.Auth");
        services.AddSingleton<KeyProtectionCertificate>();
        // Resolved lazily; tests can replace the provider without touching the user's key ring.
        services.AddSingleton<IDataProtectionProvider>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<KeyProtectionOptions>>().Value;
            var certificate = provider.GetRequiredService<KeyProtectionCertificate>().Certificate;
            var directory = new DirectoryInfo(options.KeyDirectory);
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("O diretório de chaves não pode ser um link.");
            }
            if (OperatingSystem.IsWindows())
            {
                directory.Create();
                var user = WindowsIdentity.GetCurrent().User
                    ?? throw new InvalidOperationException("Não foi possível identificar o usuário do processo.");
                var access = new DirectorySecurity();
                access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    inheritance, PropagationFlags.None, AccessControlType.Allow));
                access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
                directory.SetAccessControl(access);
            }
            else
            {
                const UnixFileMode permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Directory.CreateDirectory(directory.FullName, permissions);
                File.SetUnixFileMode(directory.FullName, permissions);
            }
            return DataProtectionProvider.Create(directory, builder =>
            {
                builder.SetApplicationName("Nexora.Auth");
                if (certificate is not null)
                {
                    builder.ProtectKeysWithCertificate(certificate);
                }
                else if (OperatingSystem.IsWindows())
                {
                    builder.ProtectKeysWithDpapi(protectToLocalMachine: false);
                }
                else if (!environment.IsDevelopment())
                {
                    throw new InvalidOperationException("Configure proteção por certificado para as chaves.");
                }
            });
        });
        return services;
    }
}

internal sealed class KeyProtectionCertificate : IDisposable
{
    internal X509Certificate2? Certificate { get; }

    public KeyProtectionCertificate(IOptions<KeyProtectionOptions> options)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            return;
        }
        try
        {
            Certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath,
                settings.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            using var rsa = Certificate.GetRSAPrivateKey();
            if (rsa is null)
            {
                throw new CryptographicException();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException)
        {
            Certificate?.Dispose();
            throw new InvalidOperationException("Não foi possível carregar o certificado RSA com chave privada para Data Protection.");
        }
    }

    public void Dispose() => Certificate?.Dispose();
}
