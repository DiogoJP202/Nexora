using System.Net;
using System.Security.Cryptography;

namespace Nexora.Mobile.Core;

public enum OutboxState { Pending = 0, CreateInFlight = 1, Uploading = 2, Finalizing = 3, Completed = 4, Failed = 5 }
public sealed record UploadOutboxItem(Guid Id, string ScopeKey, string OriginalName, long Length, string Sha256,
    string SourcePath, Guid? ServerUploadId, OutboxState State, DateTimeOffset CreatedAt,
    int[] ConfirmedChunks, AssetSnapshot? Result = null, string? FailureCode = null);
public sealed record UploadProgress(Guid Id, int ConfirmedChunks, int TotalChunks, long ConfirmedBytes, long TotalBytes);

// Only explicit foreground ResumeAsync calls send bytes. No timers or background jobs run here.
public sealed class UploadOutbox
{
    private readonly IPrivateFileStore files;
    private readonly NexoraClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly long maximumFileBytes;
    public UploadOutbox(string appPrivateRoot, NexoraClient client, long maximumFileBytes = 20L * 1024 * 1024 * 1024)
        : this(new AppPrivateFileStore(appPrivateRoot), client, maximumFileBytes) { }
    public UploadOutbox(IPrivateFileStore files, NexoraClient client, long maximumFileBytes = 20L * 1024 * 1024 * 1024)
    { this.files = files; this.client = client; this.maximumFileBytes = maximumFileBytes; }
    private ServerScope Scope => client.Scope ?? throw new InvalidOperationException("Configure o servidor primeiro.");
    private static string Folder(ServerScope scope) => $"outbox/{scope.Key}";
    private static string Metadata(ServerScope scope, Guid id) => $"{Folder(scope)}/{id:N}.json";

    public Task<UploadOutboxItem> EnqueueAsync(string originalName, Stream source, CancellationToken cancellationToken = default) =>
        client.ExecuteInScopeAsync(() => EnqueueCoreAsync(originalName, source, cancellationToken), cancellationToken);

    private async Task<UploadOutboxItem> EnqueueCoreAsync(string originalName, Stream source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originalName) || originalName.Length > 255 || originalName.Any(char.IsControl) ||
            originalName.IndexOfAny(['/', '\\']) >= 0 || originalName is "." or "..")
            throw new ArgumentException("Nome do arquivo inválido.", nameof(originalName));
        var scope = Scope;
        var id = Guid.NewGuid();
        var sourcePath = $"{Folder(scope)}/{id:N}.source";
        try
        {
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = await files.CreateAsync(sourcePath, cancellationToken))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    length = checked(length + read);
                    if (length > maximumFileBytes) throw new InvalidDataException("Arquivo acima do limite.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await destination.FlushAsync(cancellationToken);
                if (destination is FileStream disk) disk.Flush(flushToDisk: true);
            }
            EnsureScope(scope);
            var item = new UploadOutboxItem(id, scope.Key, originalName, length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), sourcePath, null, OutboxState.Pending,
                DateTimeOffset.UtcNow, []);
            await PrivateJson.WriteAsync(files, Metadata(scope, id), item, cancellationToken);
            return item;
        }
        catch { files.Delete(sourcePath); throw; }
    }

    public Task<IReadOnlyList<UploadOutboxItem>> ListAsync(CancellationToken cancellationToken = default) =>
        client.ExecuteInScopeAsync(() => ListCoreAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<UploadOutboxItem>> ListCoreAsync(CancellationToken cancellationToken)
    {
        var scope = Scope;
        var result = new List<UploadOutboxItem>();
        foreach (var path in files.List(Folder(scope), "*.json"))
        {
            var item = await PrivateJson.ReadAsync<UploadOutboxItem>(files, path, cancellationToken);
            if (item is not null && item.ScopeKey == scope.Key) result.Add(item);
        }
        return result.OrderBy(item => item.CreatedAt).ToArray();
    }

    public Task<UploadOutboxItem> ResumeAsync(Guid id, CancellationToken cancellationToken = default,
        IProgress<UploadProgress>? progress = null) =>
        client.ExecuteInScopeAsync(() => ResumeCoreAsync(id, cancellationToken, progress), cancellationToken);

    private async Task<UploadOutboxItem> ResumeCoreAsync(Guid id, CancellationToken cancellationToken, IProgress<UploadProgress>? progress)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var scope = Scope;
            var item = await ReadItemAsync(scope, id, cancellationToken);
            if (item.State == OutboxState.Completed) return item;
            UploadSnapshot? upload = null;
            if (item.ServerUploadId is { } serverId)
            {
                try { upload = await client.GetUploadAsync(serverId, cancellationToken); }
                catch (NexoraApiException error) when (IsMissingUpload(error))
                {
                    // A database restore can remove a session already known locally.
                    // Persist the reset before reusing the same client request UUID.
                    item = item with { ServerUploadId = null, State = OutboxState.Pending,
                        ConfirmedChunks = [], Result = null, FailureCode = null };
                    await SaveAsync(scope, item, cancellationToken);
                }
            }
            if (upload is null)
            {
                await ValidateSourceAsync(item, cancellationToken);
                item = item with { State = OutboxState.CreateInFlight };
                await SaveAsync(scope, item, cancellationToken);
                // Id is persisted before the first POST and reused on every retry. The
                // server's owner + ClientRequestId unique key resolves lost responses.
                upload = await client.CreateUploadAsync(new CreateUploadRequest(item.OriginalName, item.Length, item.Sha256, item.Id), cancellationToken);
                item = item with { ServerUploadId = upload.Id, State = OutboxState.Uploading };
                await SaveAsync(scope, item, cancellationToken);
            }
            ValidateUpload(item, upload);
            item = await ApplyAsync(scope, item, upload, cancellationToken);
            if (upload.State != UploadState.Open) return item;
            await ValidateSourceAsync(item, cancellationToken);
            var confirmed = upload.ConfirmedChunks.ToHashSet();
            progress?.Report(Progress(item, upload, confirmed));
            await using var original = await files.OpenReadAsync(item.SourcePath, cancellationToken);
            if (!original.CanSeek) throw new InvalidDataException("A cópia privada deve permitir retomada.");
            for (var number = 0; number < upload.ChunkCount; number++)
            {
                if (confirmed.Contains(number)) continue;
                EnsureScope(scope);
                var offset = checked((long)number * upload.ChunkSize);
                var length = Math.Min(upload.ChunkSize, item.Length - offset);
                original.Position = offset;
                var hash = await HashRangeAsync(original, length, cancellationToken);
                original.Position = offset;
                using var range = new LimitedReadStream(original, length);
                var receipt = await client.PutChunkAsync(upload.Id, number, range, length, hash, cancellationToken);
                if (receipt.Number != number || receipt.Size != length || receipt.Sha256 != hash)
                    throw new InvalidDataException("Confirmação de chunk inválida.");
                confirmed.Add(number);
                item = item with { ConfirmedChunks = confirmed.Order().ToArray() };
                await SaveAsync(scope, item, cancellationToken);
                progress?.Report(Progress(item, upload, confirmed));
            }
            await original.DisposeAsync();
            // Complete is idempotent. A lost response is resolved by GET on the next foreground attempt.
            upload = await client.CompleteUploadAsync(upload.Id, cancellationToken);
            return await ApplyAsync(scope, item, upload, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        client.ExecuteInScopeAsync(() => RemoveCoreAsync(id, cancellationToken), cancellationToken);

    private async Task RemoveCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var scope = Scope;
            var item = await ReadItemAsync(scope, id, cancellationToken);
            if (item.ServerUploadId is null && item.State == OutboxState.CreateInFlight)
            {
                // The initial POST may have committed even if its response was lost.
                // Recover that reservation before deleting its local identity/source.
                var upload = await client.CreateUploadAsync(new CreateUploadRequest(
                    item.OriginalName, item.Length, item.Sha256, item.Id), cancellationToken);
                item = await ApplyAsync(scope, item, upload, cancellationToken);
            }
            if (item.ServerUploadId is { } server && item.State is not (OutboxState.Completed or OutboxState.Failed))
            {
                try { await client.CancelUploadAsync(server, cancellationToken); }
                catch (NexoraApiException error) when (IsMissingUpload(error)) { }
            }
            EnsureScope(scope);
            files.Delete(item.SourcePath);
            files.Delete(Metadata(scope, id));
        }
        finally { gate.Release(); }
    }

    private async Task<UploadOutboxItem> ApplyAsync(ServerScope scope, UploadOutboxItem item, UploadSnapshot upload, CancellationToken cancellationToken)
    {
        ValidateUpload(item, upload);
        var state = upload.State switch
        {
            UploadState.Open => OutboxState.Uploading,
            UploadState.Finalizing => OutboxState.Finalizing,
            UploadState.Completed => OutboxState.Completed,
            _ => OutboxState.Failed
        };
        item = item with { ServerUploadId = upload.Id, State = state, ConfirmedChunks = upload.ConfirmedChunks,
            Result = upload.Result, FailureCode = upload.FailureCode };
        await SaveAsync(scope, item, cancellationToken);
        if (state == OutboxState.Completed) files.Delete(item.SourcePath);
        return item;
    }

    private static void ValidateUpload(UploadOutboxItem item, UploadSnapshot upload)
    {
        if (upload.Id == Guid.Empty || (item.ServerUploadId is { } known && known != upload.Id) ||
            upload.ExpectedLength != item.Length || upload.ChunkSize is <= 0 or > 64 * 1024 * 1024 ||
            upload.ChunkCount != (item.Length + upload.ChunkSize - 1) / upload.ChunkSize ||
            upload.ConfirmedChunks.Any(chunk => chunk < 0 || chunk >= upload.ChunkCount) || !Enum.IsDefined(upload.State))
            throw new InvalidDataException("Sessão de upload inválida.");
    }
    private async Task ValidateSourceAsync(UploadOutboxItem item, CancellationToken cancellationToken)
    {
        if (!files.Exists(item.SourcePath) || files.Length(item.SourcePath) != item.Length)
            throw new InvalidDataException("A cópia privada do arquivo não está disponível.");
        await using var input = await files.OpenReadAsync(item.SourcePath, cancellationToken);
        if (await HashRangeAsync(input, item.Length, cancellationToken) != item.Sha256)
            throw new InvalidDataException("A cópia privada do arquivo mudou.");
    }
    private static async Task<string> HashRangeAsync(Stream input, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer.AsSpan(0, read));
            length -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private async Task<UploadOutboxItem> ReadItemAsync(ServerScope scope, Guid id, CancellationToken cancellationToken)
    {
        var item = await PrivateJson.ReadAsync<UploadOutboxItem>(files, Metadata(scope, id), cancellationToken);
        if (item is null || item.Id != id || item.ScopeKey != scope.Key || item.SourcePath != $"{Folder(scope)}/{id:N}.source")
            throw new InvalidDataException("Item de upload indisponível para esta conta.");
        return item;
    }
    private async Task SaveAsync(ServerScope scope, UploadOutboxItem item, CancellationToken cancellationToken)
    {
        EnsureScope(scope);
        await PrivateJson.WriteAsync(files, Metadata(scope, item.Id), item, cancellationToken);
    }
    private static bool IsMissingUpload(NexoraApiException error) =>
        error.StatusCode == HttpStatusCode.NotFound && error.Code == "resource_not_found";
    private void EnsureScope(ServerScope expected)
    {
        if (Scope.Key != expected.Key) throw new InvalidOperationException("A conta ou o servidor mudou durante o upload.");
    }
    private static UploadProgress Progress(UploadOutboxItem item, UploadSnapshot upload, HashSet<int> chunks) =>
        new(item.Id, chunks.Count, upload.ChunkCount,
            chunks.Sum(number => Math.Min((long)upload.ChunkSize, item.Length - (long)number * upload.ChunkSize)), item.Length);
}

internal sealed class LimitedReadStream : Stream
{
    private readonly Stream inner;
    private readonly long length;
    private long remaining;
    public LimitedReadStream(Stream inner, long length) { this.inner = inner; this.length = length; remaining = length; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => length - remaining; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (remaining == 0) return 0;
        var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        remaining -= read;
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (remaining == 0) return 0;
        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
        remaining -= read;
        return read;
    }
    protected override void Dispose(bool disposing) { } // Outbox owns the original stream.
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
