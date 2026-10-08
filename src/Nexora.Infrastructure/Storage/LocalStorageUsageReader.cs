using Microsoft.Extensions.Options;
using Nexora.Application.Storage;
using Nexora.Infrastructure.Configuration;

namespace Nexora.Infrastructure.Storage;

public sealed class LocalStorageUsageReader(IOptions<StorageOptions> options) : IStorageUsageReader
{
    private readonly StorageFileSystem fileSystem = new(options.Value.RootPath);

    public Task<StorageUsage> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(options.Value.RootPath);
        fileSystem.ResolveFile("temp/.volume-check", createParents: false);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var drive = DriveInfo.GetDrives().Where(drive =>
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(drive.Name));
            return string.Equals(root, prefix, comparison)
                || root.StartsWith(Path.EndsInDirectorySeparator(prefix) ? prefix : prefix + Path.DirectorySeparatorChar, comparison);
        }).OrderByDescending(drive => drive.Name.Length).FirstOrDefault()
            ?? throw new IOException("The configured storage volume is unavailable.");

        long Sum(string category)
        {
            long size = 0;
            foreach (var path in fileSystem.EnumerateFiles(category))
            {
                cancellationToken.ThrowIfCancellationRequested();
                size = checked(size + (fileSystem.GetLength(path) ?? 0));
            }
            return size;
        }
        var blobs = Sum("blobs");
        var derivatives = checked(Sum("thumbnails") + Sum("previews"));
        var temporary = Sum("temp");
        return Task.FromResult(new StorageUsage(drive.TotalSize, drive.AvailableFreeSpace, blobs, derivatives, temporary));
    }
}
