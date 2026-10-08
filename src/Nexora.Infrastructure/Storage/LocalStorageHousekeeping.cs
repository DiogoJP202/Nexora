using Microsoft.Extensions.Options;
using Nexora.Application.Storage;
using Nexora.Infrastructure.Configuration;

namespace Nexora.Infrastructure.Storage;

public sealed class LocalStorageHousekeeping(IOptions<StorageOptions> options) : IStorageHousekeeping
{
    private readonly StorageFileSystem fileSystem = new(options.Value.RootPath);

    public Task<int> RemoveOrphansAsync(IReadOnlySet<Guid> referencedTemporaryIds, DateTimeOffset olderThan,
        CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var path in fileSystem.EnumerateFiles("temp"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filename = Path.GetFileName(path);
            var extension = Path.GetExtension(filename);
            if (extension is not (".chunk" or ".part" or ".publishing")) continue;
            var identity = Path.GetFileNameWithoutExtension(filename);
            if (identity.Length != 32 || !Guid.TryParseExact(identity, "N", out var id)
                || id == Guid.Empty || id.ToString("N") != identity || referencedTemporaryIds.Contains(id)) continue;
            if (File.GetLastWriteTimeUtc(path) >= olderThan.UtcDateTime) continue;
            fileSystem.DeleteInactive(path);
            removed++;
        }
        return Task.FromResult(removed);
    }
}
