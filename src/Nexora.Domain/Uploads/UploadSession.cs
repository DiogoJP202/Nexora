using Nexora.Domain.Content;
using Nexora.Domain.Jobs;

namespace Nexora.Domain.Uploads;

public sealed class UploadSession
{
    private UploadSession() { }

    public UploadSession(Guid id, Guid ownerId, Guid deviceId, string originalName, long expectedLength,
        string? expectedSha256, int chunkSize, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || ownerId == Guid.Empty || deviceId == Guid.Empty)
            throw new ArgumentException("An upload requires valid identifiers.");
        Asset.ValidateOriginalName(originalName);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        if (expectedLength > long.MaxValue / 2 || chunkSize > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        var chunkCount = expectedLength / chunkSize + (expectedLength % chunkSize == 0 ? 0 : 1);
        if (chunkCount > 65536) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (expectedSha256 is not null) ValidateHash(expectedSha256);
        RequireUtc(createdAt);

        Id = id;
        OwnerId = ownerId;
        DeviceId = deviceId;
        OriginalName = originalName;
        ExpectedLength = expectedLength;
        ExpectedSha256 = expectedSha256;
        ChunkSize = chunkSize;
        ChunkCount = (int)chunkCount;
        ReservedBytes = checked(expectedLength * 2);
        State = UploadState.Open;
        CreatedAt = LastActivityAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string OriginalName { get; private set; } = string.Empty;
    public long ExpectedLength { get; private set; }
    public string? ExpectedSha256 { get; private set; }
    public int ChunkSize { get; private set; }
    public int ChunkCount { get; private set; }
    public UploadState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public long ReservedBytes { get; private set; }
    public Guid? AssemblyTemporaryId { get; private set; }
    public long? AssemblyLength { get; private set; }
    public string? AssemblySha256 { get; private set; }
    public string? AssemblyMimeType { get; private set; }
    public Guid? ResultAssetId { get; private set; }
    public DateTimeOffset? ResultPurgedAt { get; private set; }
    public string? FailureCode { get; private set; }
    public ICollection<UploadChunk> Chunks { get; private set; } = [];
    public BackgroundJob? Job { get; private set; }
    public Asset? ResultAsset { get; private set; }

    public long ExpectedChunkLength(int number)
    {
        if (number < 0 || number >= ChunkCount) throw new ArgumentOutOfRangeException(nameof(number));
        return Math.Min(ChunkSize, ExpectedLength - (long)number * ChunkSize);
    }

    public void Touch(DateTimeOffset now)
    {
        RequireUtc(now);
        if (now > LastActivityAt) LastActivityAt = now;
    }

    public void BeginFinalization(DateTimeOffset now)
    {
        RequireUtc(now);
        if (State != UploadState.Open) throw new InvalidOperationException("The upload is not open.");
        State = UploadState.Finalizing;
        Touch(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        RequireUtc(now);
        if (State is not (UploadState.Open or UploadState.Finalizing or UploadState.Cancelled))
            throw new InvalidOperationException("The upload cannot be cancelled.");
        State = UploadState.Cancelled;
        Touch(now);
    }

    public void Expire(DateTimeOffset now)
    {
        RequireUtc(now);
        if (State != UploadState.Open) throw new InvalidOperationException("Only an open upload can expire.");
        State = UploadState.Expired;
        Touch(now);
    }

    public void SaveAssembly(Guid temporaryId, long length, string sha256, string mimeType, DateTimeOffset now)
    {
        RequireUtc(now);
        if (State != UploadState.Finalizing) throw new InvalidOperationException("The upload is not finalizing.");
        if (temporaryId == Guid.Empty || length != ExpectedLength)
            throw new ArgumentException("The assembly identity is invalid.");
        ValidateHash(sha256);
        if (ExpectedSha256 is not null && ExpectedSha256 != sha256)
            throw new ArgumentException("The assembly identity is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        if (mimeType.Length > Blob.MaximumMimeTypeLength || mimeType.Any(char.IsControl))
            throw new ArgumentException("The assembly MIME type is invalid.");
        if (AssemblyTemporaryId is not null && (AssemblyTemporaryId != temporaryId || AssemblySha256 != sha256
            || AssemblyLength != length || AssemblyMimeType != mimeType))
            throw new InvalidOperationException("An assembly is already recorded.");
        AssemblyTemporaryId = temporaryId;
        AssemblyLength = length;
        AssemblySha256 = sha256;
        AssemblyMimeType = mimeType;
        Touch(now);
    }

    public void Complete(Guid assetId, DateTimeOffset now)
    {
        RequireUtc(now);
        if (State != UploadState.Finalizing || assetId == Guid.Empty)
            throw new InvalidOperationException("The upload cannot complete.");
        ResultAssetId = assetId;
        FailureCode = null;
        State = UploadState.Completed;
        Touch(now);
    }

    public void Fail(string code, DateTimeOffset now)
    {
        RequireUtc(now);
        if (State is not (UploadState.Open or UploadState.Finalizing))
            throw new InvalidOperationException("The upload cannot fail.");
        ValidateFailureCode(code);
        FailureCode = code;
        State = UploadState.Failed;
        Touch(now);
    }

    public void MarkResultPurged(DateTimeOffset now)
    {
        RequireUtc(now);
        if (State != UploadState.Completed || now < LastActivityAt)
            throw new InvalidOperationException("Only a completed upload can lose its purged result.");
        if (ResultPurgedAt is not null) return;
        if (ResultAssetId is null) throw new InvalidOperationException("The completed result is missing.");
        ResultAssetId = null;
        ResultAsset = null;
        ResultPurgedAt = now;
    }

    public void FinishCleanup()
    {
        if (State is UploadState.Open or UploadState.Finalizing)
            throw new InvalidOperationException("An active upload cannot release its reservation.");
        AssemblyTemporaryId = null;
        AssemblyLength = null;
        AssemblySha256 = null;
        AssemblyMimeType = null;
        ReservedBytes = 0;
    }

    public static void ValidateHash(string sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64 || sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("The SHA-256 digest must be canonical hexadecimal.");
    }

    public static void ValidateFailureCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 64 || code.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
            throw new ArgumentException("The failure code is invalid.");
    }

    private static void RequireUtc(DateTimeOffset timestamp)
    {
        if (timestamp.Offset != TimeSpan.Zero) throw new ArgumentException("The timestamp must be UTC.");
    }
}
