using System.Buffers.Binary;
using Nexora.Application.Images;

namespace Nexora.Infrastructure.Images;

internal static class ImageContainerValidator
{
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static ReadOnlySpan<byte> JpegStart => [0xff, 0xd8];
    private static ReadOnlySpan<byte> JpegEnd => [0xff, 0xd9];
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Validate(Stream original, string mimeType, CancellationToken cancellationToken)
    {
        try
        {
            if (mimeType is not ("image/jpeg" or "image/png" or "image/webp"))
                throw new ImageProcessingException("image_format_unsupported");
            original.Position = 0;
            Span<byte> signature = stackalloc byte[12];
            original.ReadExactly(signature);
            var detected = signature[..2].SequenceEqual(JpegStart) ? "image/jpeg"
                : signature[..8].SequenceEqual(PngSignature) ? "image/png"
                : signature[..4].SequenceEqual("RIFF"u8) && signature[8..].SequenceEqual("WEBP"u8) ? "image/webp"
                : null;
            if (detected is null) throw Invalid();
            if (detected != mimeType) throw new ImageProcessingException("image_format_unsupported");
            switch (mimeType)
            {
                case "image/jpeg":
                    Span<byte> marker = stackalloc byte[2];
                    original.Position = 0;
                    original.ReadExactly(marker);
                    if (!marker.SequenceEqual(JpegStart)) throw Invalid();
                    original.Position = original.Length - 2;
                    original.ReadExactly(marker);
                    if (!marker.SequenceEqual(JpegEnd)) throw Invalid();
                    break;
                case "image/png": ValidatePng(original, cancellationToken); break;
                case "image/webp": ValidateWebp(original, cancellationToken); break;
                default: throw new ImageProcessingException("image_format_unsupported");
            }
        }
        catch (EndOfStreamException) { throw Invalid(); }
        finally { original.Position = 0; }
    }

    private static void ValidatePng(Stream original, CancellationToken cancellationToken)
    {
        original.Position = 0;
        Span<byte> header = stackalloc byte[8];
        original.ReadExactly(header);
        if (!header.SequenceEqual(PngSignature)) throw Invalid();
        Span<byte> checksum = stackalloc byte[4];
        var buffer = new byte[64 * 1024];
        var first = true;
        var seenData = false;
        while (original.Position < original.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            original.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = header[4..];
            if (length > int.MaxValue || length + 4L > original.Length - original.Position
                || (first && (!type.SequenceEqual("IHDR"u8) || length != 13))) throw Invalid();
            first = false;
            if (type.SequenceEqual("acTL"u8)) throw new ImageProcessingException("image_animation_unsupported");
            var critical = (type[0] & 0x20) == 0;
            var crc = UpdateCrc(uint.MaxValue, type);
            uint remaining = length;
            if (critical)
            {
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(remaining, (uint)buffer.Length);
                    original.ReadExactly(buffer.AsSpan(0, count));
                    crc = UpdateCrc(crc, buffer.AsSpan(0, count));
                    remaining -= (uint)count;
                }
            }
            else original.Seek(length, SeekOrigin.Current);
            original.ReadExactly(checksum);
            if (critical && BinaryPrimitives.ReadUInt32BigEndian(checksum) != ~crc) throw Invalid();
            if (type.SequenceEqual("IDAT"u8)) seenData = true;
            if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !seenData || original.Position != original.Length) throw Invalid();
                return;
            }
        }
        throw Invalid();
    }

    private static void ValidateWebp(Stream original, CancellationToken cancellationToken)
    {
        original.Position = 0;
        Span<byte> header = stackalloc byte[12];
        original.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 8L != original.Length) throw Invalid();
        var seenData = false;
        while (original.Position < original.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            original.ReadExactly(header[..8]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            var paddedLength = length + (long)(length & 1);
            if (paddedLength > original.Length - original.Position) throw Invalid();
            if (header[..4].SequenceEqual("ANIM"u8) || header[..4].SequenceEqual("ANMF"u8))
                throw new ImageProcessingException("image_animation_unsupported");
            if (header[..4].SequenceEqual("VP8 "u8) || header[..4].SequenceEqual("VP8L"u8)) seenData = true;
            original.Seek(paddedLength, SeekOrigin.Current);
        }
        if (!seenData) throw Invalid();
    }

    private static ImageProcessingException Invalid() => new("image_invalid");

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var crc = index;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
            table[index] = crc;
        }
        return table;
    }
}
