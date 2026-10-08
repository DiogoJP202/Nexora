using System.Buffers.Binary;
using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.IO;

namespace Nexora.Infrastructure.Images;

internal static class ImageCaptureMetadataReader
{
    public static (DateTime? Local, DateTimeOffset? Utc) Read(Stream original, string mimeType, CancellationToken cancellationToken)
    {
        try
        {
            original.Position = 0;
            // Restrict extraction to EXIF. General PNG readers also inflate compressed text/ICC,
            // which is unnecessary for capture dates and can exceed the image's input limit.
            var directories = mimeType == "image/jpeg"
                ? JpegMetadataReader.ReadMetadata(original, [new ExifReader()])
                : ReadChunkExif(original, mimeType, cancellationToken);
            var captureDirectories = directories.OfType<ExifSubIfdDirectory>().ToArray();
            // Multiple conflicting capture records have no unambiguous provenance.
            if (captureDirectories.Length != 1) return (null, null);
            var directory = captureDirectories[0];
            var text = directory.GetString(ExifDirectoryBase.TagDateTimeOriginal)?.TrimEnd('\0');
            if (!DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)) return (null, null);
            var local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);

            var subsecond = directory.GetString(ExifDirectoryBase.TagSubsecondTimeOriginal)?.TrimEnd('\0').Trim();
            if (!string.IsNullOrEmpty(subsecond) && subsecond.All(c => c is >= '0' and <= '9'))
            {
                var digits = subsecond.Length <= 7 ? subsecond.PadRight(7, '0') : subsecond[..7];
                if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                    local = local.AddTicks(ticks);
            }

            var offsetText = directory.GetString(ExifDirectoryBase.TagTimeZoneOriginal)?.TrimEnd('\0');
            if (!TryOffset(offsetText, out var offset)) return (local, null);
            try { return (local, new DateTimeOffset(local, offset).ToUniversalTime()); }
            catch (ArgumentException) { return (local, null); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && exception is not OperationCanceledException)
        {
            // Metadata is optional; a valid decoded image remains usable without it.
            return (null, null);
        }
    }

    private static IReadOnlyList<MetadataExtractor.Directory> ReadChunkExif(Stream original, string mimeType,
        CancellationToken cancellationToken)
    {
        var isPng = mimeType == "image/png";
        original.Position = isPng ? 8 : 12;
        Span<byte> header = stackalloc byte[8];
        byte[]? exif = null;
        while (original.Position < original.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            original.ReadExactly(header);
            var length = isPng ? BinaryPrimitives.ReadUInt32BigEndian(header)
                : BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var type = isPng ? header[4..] : header[..4];
            var padding = isPng ? 4L : length & 1;
            if (length + padding > original.Length - original.Position) return [];
            if (type.SequenceEqual(isPng ? "eXIf"u8 : "EXIF"u8))
            {
                // Large optional metadata or multiple conflicting records do not prevent previews.
                if (exif is not null || length > 1024 * 1024) return [];
                exif = new byte[(int)length];
                original.ReadExactly(exif);
                original.Seek(padding, SeekOrigin.Current);
            }
            else original.Seek(length + padding, SeekOrigin.Current);
        }
        if (exif is null) return [];
        var baseOffset = ExifReader.StartsWithJpegExifPreamble(exif) ? ExifReader.JpegSegmentPreambleLength : 0;
        return new ExifReader().Extract(new ByteArrayReader(exif, baseOffset: baseOffset), baseOffset);
    }

    private static bool TryOffset(string? value, out TimeSpan offset)
    {
        offset = default;
        if (value is not { Length: 6 } || value[0] is not ('+' or '-') || value[3] != ':'
            || value[1] is not (>= '0' and <= '9') || value[2] is not (>= '0' and <= '9')
            || value[4] is not (>= '0' and <= '9') || value[5] is not (>= '0' and <= '9')) return false;
        var hours = int.Parse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var minutes = int.Parse(value.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        if (hours > 14 || minutes > 59 || (hours == 14 && minutes != 0)) return false;
        // Treat negative zero conservatively; +00:00 explicitly identifies UTC.
        if (value == "-00:00") return false;
        offset = TimeSpan.FromMinutes((hours * 60 + minutes) * (value[0] == '-' ? -1 : 1));
        return true;
    }
}
