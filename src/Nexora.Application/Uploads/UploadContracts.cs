using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Domain.Uploads;
using Nexora.Domain.Jobs;

namespace Nexora.Application.Uploads;

public sealed record CreateUploadRequest(string OriginalName, long ExpectedLength, string? ExpectedSha256 = null);
public sealed record UploadOperationSummary(Guid Id, BackgroundJobState State, int Attempts, string? FailureCode);
public sealed record UploadSnapshot(Guid Id, string OriginalName, long ExpectedLength, int ChunkSize,
    int ChunkCount, UploadState State, int[] ConfirmedChunks, DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt, AssetSnapshot? Result, string? FailureCode, UploadOperationSummary? Operation,
    DateTimeOffset? ResultPurgedAt = null);
public sealed record UploadChunkReceipt(int Number, long Size, string Sha256, bool Reused);

public interface IUploadService
{
    Task<UploadSnapshot> CreateAsync(Guid ownerId, Guid deviceId, CreateUploadRequest request, CancellationToken cancellationToken);
    Task<UploadSnapshot?> GetAsync(Guid ownerId, Guid uploadId, CancellationToken cancellationToken);
    Task<UploadChunkReceipt> PutChunkAsync(Guid ownerId, Guid uploadId, int number, Stream content,
        string? expectedSha256, CancellationToken cancellationToken);
    Task<UploadSnapshot?> CompleteAsync(Guid ownerId, Guid uploadId, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid ownerId, Guid uploadId, CancellationToken cancellationToken);
}

public sealed class UploadOperationException(int statusCode, string code) : Exception("The upload operation was refused.")
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record UploadCompletionContext(Guid UploadId, Guid JobId, Guid LeaseToken);
public interface IStagedAssetIngestionService
{
    Task<AssetImportResult> ImportAsync(AssetImportRequest request, TemporaryObjectInfo assembly,
        UploadCompletionContext completion, CancellationToken cancellationToken);
}
public interface IUploadContentCatalog
{
    Task<AssetImportResult> CompleteUploadAsync(Guid ownerId, Guid blobId, string originalName,
        DateTimeOffset uploadedAt, UploadCompletionContext completion, CancellationToken cancellationToken);
}
