using Microsoft.Extensions.Options;
using Nexora.Application.Storage;
using Nexora.Domain.Content;
using Nexora.Infrastructure.Configuration;

namespace Nexora.Infrastructure.Storage;

public sealed class LocalFileBlobStorage : ITrackedBlobStorage
{
    private readonly StorageFileSystem fileSystem;

    public LocalFileBlobStorage(IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        fileSystem = new StorageFileSystem(options.Value.RootPath);
    }

    public Task<BlobPublicationResult> PublishAsync(BlobStorageKey key, Stream content,
        long expectedLength, string expectedSha256, CancellationToken cancellationToken)
        => PublishWithAttemptAsync(key, new TemporaryObjectKey(Guid.NewGuid()), content, expectedLength,
            expectedSha256, cancellationToken);

    public async Task<BlobPublicationResult> PublishWithAttemptAsync(BlobStorageKey key, TemporaryObjectKey attemptKey, Stream content,
        long expectedLength, string expectedSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        StoredContentInspector.ValidateExpectedHash(expectedSha256);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = fileSystem.ResolveFile(key.ToString(), createParents: true);
        if (fileSystem.GetLength(destination) is not null)
        {
            // A retry after an ambiguous publication must not allocate a third full copy.
            var identity = await StoredContentInspector.InspectAsync(content, destination: null, expectedLength,
                enforceIdentityLength: true, cancellationToken);
            if (identity.Length != expectedLength || identity.Sha256 != expectedSha256)
                throw new StorageIntegrityException();
            await VerifyExistingAsync(destination, expectedLength, expectedSha256, cancellationToken);
            fileSystem.FlushDirectory(Path.GetDirectoryName(destination)!);
            return BlobPublicationResult.AlreadyExists;
        }
        var partial = fileSystem.ResolveFile($"temp/{attemptKey}.publishing", createParents: true);
        var ownsPartial = false;
        try
        {
            await using (var output = fileSystem.CreateNew(partial))
            {
                ownsPartial = true;
                var info = await StoredContentInspector.InspectAsync(content, output, expectedLength,
                    enforceIdentityLength: true, cancellationToken);
                if (info.Length != expectedLength || !string.Equals(info.Sha256, expectedSha256,
                    StringComparison.Ordinal))
                {
                    throw new StorageIntegrityException();
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                fileSystem.MoveNew(partial, destination);
            }
            catch (IOException) when (fileSystem.GetLength(destination) is not null)
            {
                await VerifyExistingAsync(destination, expectedLength, expectedSha256, cancellationToken);
                fileSystem.Delete(partial);
                ownsPartial = false;
                fileSystem.FlushDirectory(Path.GetDirectoryName(destination)!);
                return BlobPublicationResult.AlreadyExists;
            }
            ownsPartial = false;
            fileSystem.FlushMove(partial, destination);
            return BlobPublicationResult.Published;
        }
        catch (Exception failure)
        {
            if (ownsPartial)
            {
                fileSystem.CleanUpOwnedFile(partial, failure);
            }
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(BlobStorageKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = fileSystem.ResolveFile(key.ToString(), createParents: false);
        return Task.FromResult<Stream>(fileSystem.OpenRead(path));
    }

    public Task<BlobObjectInfo?> GetInfoAsync(BlobStorageKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = fileSystem.ResolveFile(key.ToString(), createParents: false);
        var length = fileSystem.GetLength(path);
        return Task.FromResult<BlobObjectInfo?>(length is null ? null : new BlobObjectInfo(length.Value));
    }

    public Task DeleteAsync(BlobStorageKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = fileSystem.ResolveFile(key.ToString(), createParents: false);
        fileSystem.Delete(path);
        return Task.CompletedTask;
    }

    private async Task VerifyExistingAsync(string path, long expectedLength, string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stored = fileSystem.OpenRead(path);
        var info = await StoredContentInspector.InspectAsync(stored, destination: null, expectedLength,
            enforceIdentityLength: true, cancellationToken);
        if (info.Length != expectedLength || !string.Equals(info.Sha256, expectedSha256,
            StringComparison.Ordinal))
        {
            throw new StorageIntegrityException();
        }
    }
}
