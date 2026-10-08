using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Domain.Content;

namespace Nexora.Application.Uploads;

public sealed class StagedAssetIngestionService(ITemporaryStorage temporaryStorage, ITrackedBlobStorage blobStorage,
    IContentCatalog catalog, IUploadContentCatalog uploadCatalog, TimeProvider clock) : IStagedAssetIngestionService
{
    public async Task<AssetImportResult> ImportAsync(AssetImportRequest request, TemporaryObjectInfo assembly,
        UploadCompletionContext completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(completion);
        Asset.ValidateOriginalName(request.OriginalName);
        var expectedHash = request.ExpectedSha256 is null ? null : StreamingContentHash.Normalize(request.ExpectedSha256);
        var actual = await temporaryStorage.GetInfoAsync(assembly.Key, cancellationToken);
        if (actual is null || actual.Length != request.ExpectedLength || actual.Length != assembly.Length
            || actual.Sha256 != assembly.Sha256 || (expectedHash is not null && actual.Sha256 != expectedHash))
            throw new StorageIntegrityException();

        var blob = await catalog.GetOrCreateBlobAsync(request.OwnerId, actual.Sha256, actual.Length,
            actual.DetectedMimeType, clock.GetUtcNow(), cancellationToken);
        if (blob.State == BlobState.Deleting)
            throw new UploadOperationException(409, "blob_unavailable");
        if (blob.State == BlobState.Ready)
        {
            var info = await blobStorage.GetInfoAsync(blob.StorageKey, cancellationToken);
            if (info is null || info.Length != blob.Size) throw new StorageIntegrityException();
            await using var existing = await blobStorage.OpenReadAsync(blob.StorageKey, cancellationToken);
            await StreamingContentHash.VerifyAsync(existing, blob.Size, blob.Sha256, cancellationToken);
        }
        else
        {
            await using var input = await temporaryStorage.OpenReadAsync(actual.Key, cancellationToken);
            await blobStorage.PublishWithAttemptAsync(blob.StorageKey, new TemporaryObjectKey(completion.LeaseToken),
                input, blob.Size, blob.Sha256, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // The persisted assembly remains available until terminal cleanup releases the reservation.
        return await uploadCatalog.CompleteUploadAsync(request.OwnerId, blob.Id, request.OriginalName,
            clock.GetUtcNow(), completion, cancellationToken);
    }
}
