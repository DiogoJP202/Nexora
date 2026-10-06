using System.Buffers;
using System.Security.Cryptography;
using Nexora.Application.Storage;

namespace Nexora.Application.Content;

public static class StreamingContentHash
{
    public static string Normalize(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("SHA-256 must contain 64 hexadecimal characters.", nameof(value));
        return value.ToLowerInvariant();
    }

    public static async Task VerifyAsync(Stream content, long expectedLength, string expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        cancellationToken.ThrowIfCancellationRequested();
        var expected = Normalize(expectedSha256);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long length = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await content.ReadAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken);
                if (read == 0) break;
                if (read > expectedLength - length) throw new StorageIntegrityException();
                length += read;
                hash.AppendData(buffer, 0, read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (length != expectedLength || !string.Equals(actual, expected, StringComparison.Ordinal))
                throw new StorageIntegrityException();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
