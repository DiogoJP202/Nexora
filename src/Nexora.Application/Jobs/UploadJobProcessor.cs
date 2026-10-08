using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Application.Uploads;

namespace Nexora.Application.Jobs;

public sealed class UploadJobProcessor(ITrackedTemporaryStorage temporaryStorage, IUploadWorkStore workStore,
    IStagedAssetIngestionService ingestion) : IUploadJobProcessor
{
    public async Task ProcessAsync(UploadWorkItem work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        var attemptKey = new TemporaryObjectKey(work.Lease.Token);
        foreach (var previous in work.PreviousAttempts.Distinct())
        {
            if (previous == attemptKey)
            {
                throw new StorageIntegrityException();
            }
            // An active old writer refuses exclusive cleanup; retry waits instead of using 3N.
            // A registered assembly is retained, while its abandoned publication partial is removed.
            await temporaryStorage.DeleteAttemptAsync(previous, work.Assembly?.Key == previous,
                cancellationToken);
        }
        ValidateChunkLayout(work, requireAllChunks: work.Assembly is null);
        var assembly = work.Assembly;
        if (assembly is null)
        {
            await using var input = new ChunkConcatenationStream(work.Chunks, temporaryStorage);
            assembly = await temporaryStorage.CreateWithKeyAsync(attemptKey, input, work.ExpectedLength,
                cancellationToken);
            var persisted = false;
            var persistenceAttempted = false;
            try
            {
                ValidateAssemblyIdentity(work, assembly);
                persistenceAttempted = true;
                if (!await workStore.SaveAssemblyAsync(work.Lease, assembly, cancellationToken))
                {
                    persistenceAttempted = false;
                    throw new JobLeaseLostException();
                }
                persisted = true;
            }
            catch (Exception operationFailure)
            {
                // A thrown commit can have succeeded without its acknowledgment reaching us.
                // Keep that generation for retry/maintenance rather than deleting a DB reference.
                if (!persisted && !persistenceAttempted)
                {
                    try
                    {
                        await temporaryStorage.DeleteAttemptAsync(attemptKey, preserveCompleted: false,
                            CancellationToken.None);
                    }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException("Upload assembly registration and cleanup failed.",
                            operationFailure, cleanupFailure);
                    }
                }
                throw;
            }
        }
        else
        {
            ValidateAssemblyIdentity(work, assembly);
            // Confirm the persisted assembly before removing any remaining original chunks.
            await using var input = await temporaryStorage.OpenReadAsync(assembly.Key, cancellationToken);
            await StreamingContentHash.VerifyAsync(input, assembly.Length, assembly.Sha256, cancellationToken);
        }

        // Persisting the assembly first makes every chunk deletion recoverable after lease loss.
        // Releasing chunk bytes before blob publication keeps the planned two-file reservation.
        foreach (var chunk in work.Chunks.OrderBy(chunk => chunk.Number))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await temporaryStorage.DeleteAsync(chunk.Key, cancellationToken);
            if (!await workStore.RemoveChunkAsync(work.Lease, chunk.Number, cancellationToken))
            {
                throw new JobLeaseLostException();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await ingestion.ImportAsync(new AssetImportRequest(work.OwnerId, work.OriginalName,
            work.ExpectedLength, work.ExpectedSha256), assembly,
            new UploadCompletionContext(work.Lease.UploadId, work.Lease.JobId, work.Lease.Token),
            cancellationToken);
        if (result.Status == AssetImportStatus.BlobUnavailable)
        {
            throw new StorageIntegrityException();
        }
        // A terminal upload retains its assembly/reservation until durable maintenance cleanup.
    }

    private static void ValidateAssemblyIdentity(UploadWorkItem work, TemporaryObjectInfo assembly)
    {
        if (assembly.Length != work.ExpectedLength || StreamingContentHash.Normalize(assembly.Sha256) != assembly.Sha256
            || (work.ExpectedSha256 is not null
                && assembly.Sha256 != StreamingContentHash.Normalize(work.ExpectedSha256)))
        {
            throw new StorageIntegrityException();
        }
    }

    private static void ValidateChunkLayout(UploadWorkItem work, bool requireAllChunks)
    {
        if (work.ExpectedLength < 0 || work.ChunkSize <= 0 || work.ChunkCount < 0)
        {
            throw new StorageIntegrityException();
        }
        var expectedCount = work.ExpectedLength == 0 ? 0 : (work.ExpectedLength - 1) / work.ChunkSize + 1;
        if (expectedCount != work.ChunkCount || (requireAllChunks && work.Chunks.Length != work.ChunkCount))
        {
            throw new StorageIntegrityException();
        }
        var numbers = new HashSet<int>();
        foreach (var chunk in work.Chunks)
        {
            if (chunk.Number < 0 || chunk.Number >= work.ChunkCount || !numbers.Add(chunk.Number)
                || chunk.Size != Math.Min((long)work.ChunkSize,
                    work.ExpectedLength - (long)chunk.Number * work.ChunkSize))
            {
                throw new StorageIntegrityException();
            }
        }
    }
}
