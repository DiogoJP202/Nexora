using Nexora.Application.Storage;
using Nexora.Domain.Content;

namespace Nexora.Application.Content;

public sealed class AssetIngestionService(
    ITemporaryStorage temporaryStorage, IBlobStorage blobStorage, IContentCatalog catalog, TimeProvider clock)
    : IAssetIngestionService
{
    public async Task<AssetImportResult> ImportAsync(AssetImportRequest request, Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        if (request.OwnerId == Guid.Empty) throw new ArgumentException("A content owner is required.", nameof(request));
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedLength);
        Asset.ValidateOriginalName(request.OriginalName);
        var expectedHash = request.ExpectedSha256 is null ? null : StreamingContentHash.Normalize(request.ExpectedSha256);

        var staged = await temporaryStorage.CreateAsync(content, request.ExpectedLength, cancellationToken);
        Exception? operationFailure = null;
        try
        {
            if (staged.Length != request.ExpectedLength || (expectedHash is not null && expectedHash != staged.Sha256))
                throw new StorageIntegrityException();

            var blob = await catalog.GetOrCreateBlobAsync(request.OwnerId, staged.Sha256, staged.Length,
                staged.DetectedMimeType, clock.GetUtcNow(), cancellationToken);
            if (blob.State == BlobState.Deleting)
                return new AssetImportResult(AssetImportStatus.BlobUnavailable, null);
            if (blob.State == BlobState.Ready)
            {
                var info = await blobStorage.GetInfoAsync(blob.StorageKey, cancellationToken);
                if (info is null || info.Length != blob.Size) throw new StorageIntegrityException();
                await using var existing = await blobStorage.OpenReadAsync(blob.StorageKey, cancellationToken);
                await StreamingContentHash.VerifyAsync(existing, blob.Size, blob.Sha256, cancellationToken);
            }
            else
            {
                await using var input = await temporaryStorage.OpenReadAsync(staged.Key, cancellationToken);
                await blobStorage.PublishAsync(blob.StorageKey, input, blob.Size, blob.Sha256, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await catalog.CompleteAsync(request.OwnerId, blob.Id, request.OriginalName,
                clock.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            // Failed cleanup is visible to the caller; committed results remain safe to retry.
            try
            {
                await temporaryStorage.DeleteAsync(staged.Key, CancellationToken.None);
            }
            catch (Exception cleanupFailure) when (operationFailure is not null)
            {
                throw new AggregateException("Content ingestion and temporary cleanup failed.", operationFailure, cleanupFailure);
            }
        }
    }
}
