using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Storage;

namespace Nexora.IntegrationTests;

internal sealed class StorageTestFiles : IDisposable
{
    public StorageTestFiles()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "nexora-storage-it_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User!;
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var principal in new[] { owner, new SecurityIdentifier("S-1-5-18") })
            {
                security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                    AccessControlType.Allow));
            }

            new DirectoryInfo(RootPath).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(RootPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public string RootPath { get; }
    public LocalFileBlobStorage Blobs => new(Options.Create(new StorageOptions { RootPath = RootPath }));
    public LocalFileTemporaryStorage Temporary => new(Options.Create(new StorageOptions { RootPath = RootPath }));
    public IReadOnlyList<string> Files => Directory.GetFiles(RootPath, "*", SearchOption.AllDirectories);

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(RootPath);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullPath)!);
        var temporaryParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(parent, temporaryParent, comparison) ||
            !Regex.IsMatch(Path.GetFileName(fullPath), "^nexora-storage-it_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Recusada limpeza de diretório que não pertence ao fixture de storage.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}

internal sealed class StorageTestReadStream(byte[] content, int? failAfterReads = null, CancellationTokenSource? cancelAfterRead = null)
    : MemoryStream(content, writable: false)
{
    private int _reads;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (failAfterReads is { } limit && _reads >= limit)
        {
            throw new IOException("Simulated source read failure.");
        }

        _reads++;
        var read = base.ReadAsync(buffer, cancellationToken);
        cancelAfterRead?.Cancel();
        return read;
    }
}
