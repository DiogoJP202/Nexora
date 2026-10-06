using System.Text;
using Nexora.Application.Content;
using Nexora.Application.Storage;

namespace Nexora.UnitTests;

public sealed class StreamingContentHashTests
{
    private const string HelloHash = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

    [Fact]
    public async Task VerifiesKnownSha256WithoutSeekingAndNormalizesClientHash()
    {
        await using var content = new NonSeekableStream(Encoding.UTF8.GetBytes("hello"));
        await StreamingContentHash.VerifyAsync(content, 5, HelloHash.ToUpperInvariant(), CancellationToken.None);
        Assert.Equal(HelloHash, StreamingContentHash.Normalize(HelloHash.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(4, HelloHash)]
    [InlineData(6, HelloHash)]
    [InlineData(5, "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task RejectsWrongLengthOrHash(long expectedLength, string hash)
    {
        await using var content = new NonSeekableStream(Encoding.UTF8.GetBytes("hello"));
        await Assert.ThrowsAsync<StorageIntegrityException>(() =>
            StreamingContentHash.VerifyAsync(content, expectedLength, hash, CancellationToken.None));
    }

    [Fact]
    public async Task ObservesCancellationBeforeReadingTheContent()
    {
        await using var content = new NonSeekableStream(Encoding.UTF8.GetBytes("hello"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StreamingContentHash.VerifyAsync(content, 5, HelloHash, cancellation.Token));
    }

    [Fact]
    public async Task CancelledEmptyVerificationCannotSucceedEvenWhenTheSourceIgnoresCancellation()
    {
        await using var content = new CancellationIgnoringEmptyStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StreamingContentHash.VerifyAsync(content, 0,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", cancellation.Token));
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    private sealed class CancellationIgnoringEmptyStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    }
}
