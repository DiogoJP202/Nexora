using Microsoft.Extensions.Options;
using Nexora.Application.Storage;
using Nexora.Infrastructure.Configuration;

namespace Nexora.Infrastructure.Storage;

public sealed class LocalFileTemporaryStorage : ITrackedTemporaryStorage
{
    private readonly StorageFileSystem fileSystem;

    public LocalFileTemporaryStorage(IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        fileSystem = new StorageFileSystem(options.Value.RootPath);
    }

    public Task<TemporaryObjectInfo> CreateAsync(Stream content, long maximumLength,
        CancellationToken cancellationToken)
        => CreateWithKeyAsync(new TemporaryObjectKey(Guid.NewGuid()), content, maximumLength, cancellationToken);

    public async Task<TemporaryObjectInfo> CreateWithKeyAsync(TemporaryObjectKey key, Stream content, long maximumLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = fileSystem.ResolveFile($"temp/{key}.chunk", createParents: true);
        var partial = fileSystem.ResolveFile($"temp/{key}.part", createParents: true);
        var ownsPartial = false;
        var ownsDestination = false;
        try
        {
            StoredContentInspector.ContentInfo info;
            await using (var output = fileSystem.CreateNew(partial))
            {
                ownsPartial = true;
                info = await StoredContentInspector.InspectAsync(content, output, maximumLength,
                    enforceIdentityLength: false, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.MoveNew(partial, destination);
            ownsPartial = false;
            ownsDestination = true;
            fileSystem.FlushMove(partial, destination);
            ownsDestination = false;
            return new TemporaryObjectInfo(key, info.Length, info.Sha256, info.MimeType);
        }
        catch (Exception failure)
        {
            if (ownsPartial)
            {
                fileSystem.CleanUpOwnedFile(partial, failure);
            }
            if (ownsDestination)
            {
                fileSystem.CleanUpOwnedFile(destination, failure);
            }
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(TemporaryObjectKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = fileSystem.ResolveFile($"temp/{key}.chunk", createParents: false);
        return Task.FromResult<Stream>(fileSystem.OpenRead(path));
    }

    public async Task<TemporaryObjectInfo?> GetInfoAsync(TemporaryObjectKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = fileSystem.ResolveFile($"temp/{key}.chunk", createParents: false);
        if (fileSystem.GetLength(path) is null)
        {
            return null;
        }
        await using var stored = fileSystem.OpenRead(path);
        var info = await StoredContentInspector.InspectAsync(stored, destination: null, long.MaxValue,
            enforceIdentityLength: false, cancellationToken);
        return new TemporaryObjectInfo(key, info.Length, info.Sha256, info.MimeType);
    }

    public Task DeleteAsync(TemporaryObjectKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = fileSystem.ResolveFile($"temp/{key}.chunk", createParents: false);
        fileSystem.Delete(path);
        return Task.CompletedTask;
    }

    public Task DeleteAttemptAsync(TemporaryObjectKey key, bool preserveCompleted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var extension in new[] { ".part", ".publishing", ".chunk" })
        {
            if (preserveCompleted && extension == ".chunk") continue;
            var path = fileSystem.ResolveFile($"temp/{key}{extension}", createParents: false);
            fileSystem.DeleteInactive(path);
        }
        return Task.CompletedTask;
    }
}
