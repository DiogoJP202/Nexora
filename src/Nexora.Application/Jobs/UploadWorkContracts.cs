using Nexora.Application.Storage;

namespace Nexora.Application.Jobs;

public sealed record JobLease(Guid JobId, Guid UploadId, Guid Token, DateTimeOffset ExpiresAt);
public sealed record UploadChunkWork(int Number, long Size, string Sha256, TemporaryObjectKey Key);
public sealed record UploadWorkItem(JobLease Lease, Guid OwnerId, string OriginalName, long ExpectedLength,
    string? ExpectedSha256, int ChunkSize, int ChunkCount, UploadChunkWork[] Chunks, TemporaryObjectInfo? Assembly,
    TemporaryObjectKey[] PreviousAttempts);
public sealed record UploadCleanupItem(Guid UploadId, TemporaryObjectKey[] Keys);

public interface IUploadWorkStore
{
    Task<UploadWorkItem?> ClaimAsync(CancellationToken cancellationToken);
    Task<bool> RenewAsync(JobLease lease, CancellationToken cancellationToken);
    Task<bool> SaveAssemblyAsync(JobLease lease, TemporaryObjectInfo assembly, CancellationToken cancellationToken);
    Task<bool> RemoveChunkAsync(JobLease lease, int number, CancellationToken cancellationToken);
    Task<bool> FailAsync(JobLease lease, string failureCode, bool retryable, CancellationToken cancellationToken);
    Task<int> ExpireOpenAsync(CancellationToken cancellationToken);
    Task<UploadCleanupItem[]> ListCleanupAsync(int limit, CancellationToken cancellationToken);
    Task<bool> FinishCleanupAsync(Guid uploadId, CancellationToken cancellationToken);
    Task<HashSet<Guid>> ReferencedTemporaryIdsAsync(CancellationToken cancellationToken);
}

public interface IUploadJobProcessor
{
    Task ProcessAsync(UploadWorkItem work, CancellationToken cancellationToken);
}

public sealed class JobLeaseLostException() : Exception("The background job lease is no longer valid.");
