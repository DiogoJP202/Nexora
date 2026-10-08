using System.Security.Cryptography;
using Nexora.Application.Content;
using Nexora.Application.Storage;

namespace Nexora.Application.Jobs;

/// <summary>Reads confirmed chunks in order and verifies each identity before advancing.</summary>
public sealed class ChunkConcatenationStream : Stream
{
    private readonly UploadChunkWork[] chunks;
    private readonly ITemporaryStorage storage;
    private Stream? current;
    private IncrementalHash? hash;
    private int index;
    private long chunkBytesRead;
    private bool disposed;

    public ChunkConcatenationStream(IEnumerable<UploadChunkWork> chunks, ITemporaryStorage storage)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(storage);
        this.chunks = chunks.OrderBy(chunk => chunk.Number).ToArray();
        this.storage = storage;
        for (var number = 0; number < this.chunks.Length; number++)
        {
            var chunk = this.chunks[number];
            if (chunk.Number != number || chunk.Size < 0
                || StreamingContentHash.Normalize(chunk.Sha256) != chunk.Sha256)
            {
                throw new StorageIntegrityException();
            }
            _ = chunk.Key.ToString();
        }
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0)
        {
            return 0;
        }
        while (index < chunks.Length)
        {
            var chunk = chunks[index];
            if (current is null)
            {
                current = await storage.OpenReadAsync(chunk.Key, cancellationToken);
                hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                chunkBytesRead = 0;
            }
            var read = await current.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], cancellationToken);
            if (read != 0)
            {
                if (read > chunk.Size - chunkBytesRead)
                {
                    throw new StorageIntegrityException();
                }
                chunkBytesRead += read;
                hash!.AppendData(buffer.Span[..read]);
                return read;
            }
            var actualHash = Convert.ToHexStringLower(hash!.GetHashAndReset());
            if (chunkBytesRead != chunk.Size || !string.Equals(actualHash, chunk.Sha256, StringComparison.Ordinal))
            {
                throw new StorageIntegrityException();
            }
            await current.DisposeAsync();
            current = null;
            hash.Dispose();
            hash = null;
            index++;
        }
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && !disposed)
            {
                disposed = true;
                try
                {
                    current?.Dispose();
                }
                finally
                {
                    hash?.Dispose();
                }
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!disposed)
            {
                disposed = true;
                try
                {
                    if (current is not null)
                    {
                        await current.DisposeAsync();
                    }
                }
                finally
                {
                    hash?.Dispose();
                }
            }
        }
        finally
        {
            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
