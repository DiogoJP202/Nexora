using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nexora.Application.Images;
using Nexora.Application.Storage;
using Nexora.Infrastructure.Configuration;

namespace Nexora.Infrastructure.Storage;

public sealed class LocalDerivativeStorage(IOptions<StorageOptions> storageOptions, IOptions<ImageOptions> imageOptions)
    : IDerivativeStorage
{
    private readonly StorageFileSystem fileSystem = new(storageOptions.Value.RootPath);

    public async Task<DerivativeInfo> PublishAsync(DerivativeKey key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var limit = key.Kind == DerivativeKind.Thumbnail ? imageOptions.Value.ThumbnailSize : imageOptions.Value.PreviewSize;
        if (content.Length > imageOptions.Value.MaximumDerivativeBytes) throw new ImageProcessingException("image_derivative_too_large");
        ValidatePng(content.Span, limit);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        var destination = fileSystem.ResolveFile(key.ToString(), createParents: true);
        if (fileSystem.GetLength(destination) is not null)
        {
            var existing = await GetInfoAsync(key, cancellationToken);
            if (existing is null || existing.Length != content.Length || existing.Sha256 != hash) throw new StorageIntegrityException();
            fileSystem.FlushDirectory(Path.GetDirectoryName(destination)!);
            return existing;
        }
        var partial = fileSystem.ResolveFile(key + ".publishing", createParents: true);
        var owned = false;
        try
        {
            await using (var output = fileSystem.CreateNew(partial))
            {
                owned = true;
                await output.WriteAsync(content, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.MoveNew(partial, destination);
            owned = false;
            fileSystem.FlushMove(partial, destination);
            return new DerivativeInfo(content.Length, hash);
        }
        catch (Exception failure)
        {
            if (owned) fileSystem.CleanUpOwnedFile(partial, failure);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(DerivativeKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(fileSystem.OpenRead(fileSystem.ResolveFile(key.ToString(), createParents: false)));
    }

    public async Task<DerivativeInfo?> GetInfoAsync(DerivativeKey key, CancellationToken cancellationToken)
    {
        var path = fileSystem.ResolveFile(key.ToString(), createParents: false);
        var length = fileSystem.GetLength(path);
        if (length is null) return null;
        await using var input = fileSystem.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
        return new DerivativeInfo(length.Value, hash);
    }

    public Task CleanAttemptAsync(Guid generationId, bool preservePublished, CancellationToken cancellationToken)
    {
        foreach (var kind in Enum.GetValues<DerivativeKind>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new DerivativeKey(generationId, kind);
            fileSystem.DeleteInactive(fileSystem.ResolveFile(key + ".publishing", createParents: false));
            if (!preservePublished) fileSystem.DeleteInactive(fileSystem.ResolveFile(key.ToString(), createParents: false));
        }
        return Task.CompletedTask;
    }

    // Validate the PNG envelope and CRCs without running a decoder in the API/Worker.
    // Only pixels encoded by the isolated renderer may be published inline.
    private static void ValidatePng(ReadOnlySpan<byte> data, int maximumDimension)
    {
        if (data.Length < 45 || !data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) Invalid();
        var offset = 8;
        var first = true;
        var pixels = false;
        while (offset <= data.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (length > (uint)(data.Length - offset - 12)) Invalid();
            var size = (int)length;
            var typeAndData = data.Slice(offset + 4, size + 4);
            var type = typeAndData[..4];
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + size, 4));
            uint crc = uint.MaxValue;
            foreach (var value in typeAndData)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1u)));
            }
            if (~crc != expectedCrc) Invalid();
            if (first)
            {
                if (size != 13 || !type.SequenceEqual("IHDR"u8)) Invalid();
                var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8, 4));
                var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 12, 4));
                if (width == 0 || height == 0 || width > maximumDimension || height > maximumDimension) Invalid();
                first = false;
            }
            else if (type.SequenceEqual("IHDR"u8)) Invalid();
            if (type.SequenceEqual("IDAT"u8)) pixels = true;
            offset += size + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                if (size != 0 || !pixels || offset != data.Length) Invalid();
                return;
            }
        }
        Invalid();
        static void Invalid() => throw new ImageProcessingException("image_invalid_derivative");
    }
}
