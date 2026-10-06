using System.Buffers;
using System.Security.Cryptography;
using Nexora.Application.Storage;

namespace Nexora.Infrastructure.Storage;

internal static class StoredContentInspector
{
    internal const int BufferSize = 64 * 1024;

    internal sealed record ContentInfo(long Length, string Sha256, string MimeType);

    internal static void ValidateExpectedHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9')
            and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("The expected SHA-256 must be canonical lowercase hexadecimal.", nameof(value));
        }
    }

    internal static async Task<ContentInfo> InspectAsync(Stream source, Stream? destination,
        long maximumLength, bool enforceIdentityLength, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var header = new byte[12];
        var headerLength = 0;
        long length = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                if (read > maximumLength - length)
                {
                    if (enforceIdentityLength)
                    {
                        throw new StorageIntegrityException();
                    }
                    throw new StorageLimitExceededException();
                }
                length += read;
                hash.AppendData(buffer, 0, read);
                var headerBytes = Math.Min(read, header.Length - headerLength);
                buffer.AsSpan(0, headerBytes).CopyTo(header.AsSpan(headerLength));
                headerLength += headerBytes;
                if (destination is not null)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ContentInfo(length, Convert.ToHexStringLower(hash.GetHashAndReset()),
                DetectMimeType(header.AsSpan(0, headerLength)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string DetectMimeType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff)
        {
            return "image/jpeg";
        }
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (header.StartsWith(pngSignature))
        {
            return "image/png";
        }
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8)
            && header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return "application/octet-stream";
    }
}
