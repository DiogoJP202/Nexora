using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MetadataExtractor.Formats.Exif;
using Nexora.Application.Images;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Images;
using SkiaSharp;

namespace Nexora.IntegrationTests;

public sealed class ImageCodecTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public void SupportedFormatsProducePngAndRespectBothBounds(SKEncodedImageFormat format, string mimeType)
    {
        var original = Encode(2_000, 1_000, format);
        using var stream = new MemoryStream(original);
        var rendered = new SkiaImageRenderer().Render(stream, Parameters(original, mimeType), CancellationToken.None);
        Assert.Equal(2_000, rendered.Width);
        Assert.Equal(1_000, rendered.Height);
        Assert.Null(rendered.CapturedAtLocal);
        Assert.Null(rendered.CapturedAtUtc);
        AssertPngSize(rendered.Thumbnail, 256, 128);
        AssertPngSize(rendered.Preview, 1_280, 640);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public void SmallTransparentImagesRemainSmallAndKeepAlpha(SKEncodedImageFormat format, string mimeType)
    {
        var original = Encode(32, 16, format, new SKColor(200, 40, 30, 100));
        var rendered = Render(original, mimeType);
        AssertPngSize(rendered.Thumbnail, 32, 16);
        AssertPngSize(rendered.Preview, 32, 16);
        using var bitmap = SKBitmap.Decode(rendered.Preview);
        Assert.InRange(bitmap.GetPixel(16, 8).Alpha, (byte)98, (byte)102);
    }

    [Theory]
    [InlineData(1, 64, 32, "RGBY")]
    [InlineData(2, 64, 32, "GRYB")]
    [InlineData(3, 64, 32, "YBGR")]
    [InlineData(4, 64, 32, "BYRG")]
    [InlineData(5, 32, 64, "RBGY")]
    [InlineData(6, 32, 64, "BRYG")]
    [InlineData(7, 32, 64, "YGBR")]
    [InlineData(8, 32, 64, "GYRB")]
    public void AllExifOrientationsTransformPixelsAndDimensions(int orientation, int width, int height, string expected)
    {
        var original = AddExif(EncodeQuadrants(), BuildTiff((ushort)orientation));
        var rendered = Render(original, "image/jpeg");
        Assert.Equal(width, rendered.Width);
        Assert.Equal(height, rendered.Height);
        AssertPngSize(rendered.Thumbnail, width, height);
        using var bitmap = SKBitmap.Decode(rendered.Preview);
        var actual = string.Concat(ColorCode(bitmap.GetPixel(width / 4, height / 4)),
            ColorCode(bitmap.GetPixel(3 * width / 4, height / 4)),
            ColorCode(bitmap.GetPixel(width / 4, 3 * height / 4)),
            ColorCode(bitmap.GetPixel(3 * width / 4, 3 * height / 4)));
        Assert.Equal(expected, actual);
        var derivativeExif = MetadataExtractor.ImageMetadataReader.ReadMetadata(new MemoryStream(rendered.Preview))
            .OfType<ExifSubIfdDirectory>();
        Assert.Empty(derivativeExif);
    }

    [Fact]
    public void CaptureDateAndSubsecondWithOriginalOffsetProduceReliableUtcOnly()
    {
        var original = AddExif(Encode(64, 32, SKEncodedImageFormat.Jpeg),
            BuildTiff(1, "2025:12:31 23:45:10", "-03:30", "123456789"));
        var rendered = Render(original, "image/jpeg");
        var expectedLocal = new DateTime(2025, 12, 31, 23, 45, 10, DateTimeKind.Unspecified).AddTicks(1_234_567);
        Assert.Equal(expectedLocal, rendered.CapturedAtLocal);
        Assert.Equal(DateTimeKind.Unspecified, rendered.CapturedAtLocal!.Value.Kind);
        Assert.Equal(new DateTimeOffset(expectedLocal, TimeSpan.FromMinutes(-210)).ToUniversalTime(), rendered.CapturedAtUtc);
        Assert.Equal(TimeSpan.Zero, rendered.CapturedAtUtc!.Value.Offset);
        var metadata = MetadataExtractor.ImageMetadataReader.ReadMetadata(new MemoryStream(rendered.Preview));
        Assert.Empty(metadata.OfType<ExifSubIfdDirectory>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("99:99")]
    [InlineData("+14:01")]
    [InlineData("-00:00")]
    [InlineData("+03:99")]
    [InlineData("+0x:00")]
    public void MissingOrInvalidOffsetNeverGuessesTimeZone(string? offset)
    {
        var original = AddExif(Encode(32, 16, SKEncodedImageFormat.Jpeg),
            BuildTiff(1, "2024:01:02 03:04:05", offset));
        var rendered = Render(original, "image/jpeg");
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), rendered.CapturedAtLocal);
        Assert.Equal(DateTimeKind.Unspecified, rendered.CapturedAtLocal!.Value.Kind);
        Assert.Null(rendered.CapturedAtUtc);
    }

    [Fact]
    public void InvalidCaptureDateAndMalformedExifDoNotPreventValidPixels()
    {
        var jpeg = Encode(32, 16, SKEncodedImageFormat.Jpeg);
        var invalidDate = AddExif(jpeg, BuildTiff(1, "0000:99:99 25:01:01", "+03:00"));
        var rendered = Render(invalidDate, "image/jpeg");
        Assert.Null(rendered.CapturedAtLocal);
        Assert.Null(rendered.CapturedAtUtc);
        AssertPngSize(rendered.Preview, 32, 16);
        var malformed = AddExif(jpeg, [0x49, 0x49, 0x2a, 0, 0xff, 0xff, 0xff, 0xff]);
        AssertPngSize(Render(malformed, "image/jpeg").Preview, 32, 16);
    }

    [Fact]
    public void PngExifProvidesCaptureDateWithoutCopyingMetadataToDerivative()
    {
        var png = Encode(32, 16, SKEncodedImageFormat.Png);
        var original = AddPngExif(png, BuildTiff(1, "2024:01:02 03:04:05", "+00:00", "5"));
        var rendered = Render(original, "image/png");
        var expected = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddMilliseconds(500);
        Assert.Equal(expected, rendered.CapturedAtLocal);
        Assert.Equal(new DateTimeOffset(expected, TimeSpan.Zero), rendered.CapturedAtUtc);
        Assert.Empty(MetadataExtractor.ImageMetadataReader.ReadMetadata(new MemoryStream(rendered.Preview))
            .OfType<ExifSubIfdDirectory>());
    }

    [Fact]
    public void WebpExifProvidesCaptureDateAndCorrectsOrientation()
    {
        var webp = Encode(32, 16, SKEncodedImageFormat.Webp);
        var original = AddWebpExif(webp, BuildTiff(6, "2024:01:02 03:04:05", "+02:00"), 32, 16);
        var rendered = Render(original, "image/webp");
        Assert.Equal(16, rendered.Width);
        Assert.Equal(32, rendered.Height);
        var expected = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        Assert.Equal(expected, rendered.CapturedAtLocal);
        Assert.Equal(new DateTimeOffset(expected, TimeSpan.FromHours(2)).ToUniversalTime(), rendered.CapturedAtUtc);
        AssertPngSize(rendered.Preview, 16, 32);
    }

    [Fact]
    public void LimitsAndExpectedContentAreCheckedBeforeDecode()
    {
        var png = Encode(32, 16, SKEncodedImageFormat.Png);
        AssertFailure(png, Parameters(png, "image/png") with { MaximumInputBytes = png.Length - 1 }, "image_input_too_large");
        AssertFailure(png, Parameters(png, "image/png") with { ExpectedLength = png.Length + 1 }, "image_integrity_failed");
        AssertFailure(png, Parameters(png, "image/png") with { ExpectedSha256 = new string('0', 64) }, "image_integrity_failed");
        AssertFailure(png, Parameters(png, "image/png") with { MaximumPixels = 511 }, "image_pixel_limit_exceeded");
        AssertFailure(png, Parameters(png, "image/png") with { MaximumDimension = 31 }, "image_pixel_limit_exceeded");
        AssertFailure(png, Parameters(png, "image/png") with { MaximumDecodedBytes = 512 * 4 - 1 }, "image_decoded_limit_exceeded");
        AssertFailure(png, Parameters(png, "image/png") with { MaximumDerivativeBytes = 1 }, "image_derivative_too_large");
        AssertFailure(png, Parameters(png, "image/jpeg"), "image_format_unsupported");
        var gif = Encode(32, 16, SKEncodedImageFormat.Png);
        AssertFailure(gif, Parameters(gif, "image/gif"), "image_format_unsupported");
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public void TruncatedContainersAreRejectedEvenWhenSomePixelsAreDecodable(SKEncodedImageFormat format, string mimeType)
    {
        var original = Encode(64, 32, format);
        var truncated = original[..^2];
        AssertFailure(truncated, Parameters(truncated, mimeType), "image_invalid");
        var missingPixelData = original[..(original.Length / 2)];
        AssertFailure(missingPixelData, Parameters(missingPixelData, mimeType), "image_invalid");
    }

    [Fact]
    public void CorruptCriticalPngChecksumIsRejected()
    {
        var png = Encode(32, 16, SKEncodedImageFormat.Png);
        png[29] ^= 1; // IHDR CRC, after the 8-byte signature and 13-byte header data.
        AssertFailure(png, Parameters(png, "image/png"), "image_invalid");
    }

    [Fact]
    public void CancellationAndNonSeekableInputAreRejected()
    {
        var original = Encode(32, 16, SKEncodedImageFormat.Png);
        using var stream = new MemoryStream(original);
        Assert.Throws<OperationCanceledException>(() => new SkiaImageRenderer().Render(stream,
            Parameters(original, "image/png"), new CancellationToken(true)));
        using var nonSeekable = new NonSeekableInput(original);
        var error = Assert.Throws<ImageProcessingException>(() => new SkiaImageRenderer().Render(nonSeekable,
            Parameters(original, "image/png"), CancellationToken.None));
        Assert.Equal("image_invalid", error.Code);
    }

    [Fact]
    public void OptionsRefuseUnsafeLimitsOrLeaseTiming()
    {
        var validator = new ImageOptionsValidator();
        Assert.True(validator.Validate(null, new ImageOptions()).Succeeded);
        Assert.True(validator.Validate(null, new ImageOptions { MaximumPixels = 0 }).Failed);
        Assert.True(validator.Validate(null, new ImageOptions { MaximumProcessMemoryBytes = 64L * 1024 * 1024 }).Failed);
        Assert.True(validator.Validate(null, new ImageOptions { LeaseRenewInterval = TimeSpan.FromSeconds(30) }).Failed);
        Assert.True(validator.Validate(null, new ImageOptions { ProcessingTimeout = TimeSpan.FromDays(31) }).Failed);
    }

    private static RenderedImage Render(byte[] bytes, string mimeType)
    {
        using var stream = new MemoryStream(bytes);
        return new SkiaImageRenderer().Render(stream, Parameters(bytes, mimeType), CancellationToken.None);
    }

    private static ImageRenderParameters Parameters(byte[] bytes, string mimeType) => new(bytes.Length,
        Convert.ToHexStringLower(SHA256.HashData(bytes)), mimeType, 32L * 1024 * 1024, 24_000_000,
        128L * 1024 * 1024, 16_384, 256, 1_280, 8 * 1024 * 1024);

    private static void AssertFailure(byte[] bytes, ImageRenderParameters parameters, string code)
    {
        using var stream = new MemoryStream(bytes);
        var error = Assert.Throws<ImageProcessingException>(() => new SkiaImageRenderer().Render(stream, parameters, CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.False(error.Retryable);
    }

    private static void AssertPngSize(byte[] bytes, int width, int height)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
        Assert.Equal(width, codec.Info.Width);
        Assert.Equal(height, codec.Info.Height);
        Assert.Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin);
    }

    internal static byte[] Encode(int width, int height, SKEncodedImageFormat format, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(color ?? SKColors.CornflowerBlue);
        bitmap.SetImmutable();
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 100);
        Assert.NotNull(encoded);
        return encoded.ToArray();
    }

    private static byte[] EncodeQuadrants()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(64, 32, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 64; x++)
            bitmap.SetPixel(x, y, (x < 32, y < 16) switch
            {
                (true, true) => SKColors.Red, (false, true) => SKColors.Lime,
                (true, false) => SKColors.Blue, _ => SKColors.Yellow
            });
        bitmap.SetImmutable();
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        return encoded.ToArray();
    }

    private static char ColorCode(SKColor color)
    {
        if (color.Red > 200 && color.Green > 200 && color.Blue < 40) return 'Y';
        if (color.Red > 200 && color.Green < 40 && color.Blue < 40) return 'R';
        if (color.Green > 200 && color.Red < 40 && color.Blue < 40) return 'G';
        if (color.Blue > 200 && color.Red < 40 && color.Green < 40) return 'B';
        return '?';
    }

    internal static byte[] BuildTiff(ushort orientation, string? captureDate = null, string? originalOffset = null, string? subsecond = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write(8u);
        writer.Write((ushort)2);
        writer.Write((ushort)0x0112); writer.Write((ushort)3); writer.Write(1u); writer.Write(orientation); writer.Write((ushort)0);
        writer.Write((ushort)0x8769); writer.Write((ushort)4); writer.Write(1u); writer.Write(38u);
        writer.Write(0u);
        var values = new SortedDictionary<ushort, byte[]>();
        if (captureDate is not null) values[0x9003] = Encoding.ASCII.GetBytes(captureDate + '\0');
        if (originalOffset is not null) values[0x9011] = Encoding.ASCII.GetBytes(originalOffset + '\0');
        if (subsecond is not null) values[0x9291] = Encoding.ASCII.GetBytes(subsecond + '\0');
        writer.Write((ushort)values.Count);
        var externalOffset = 38u + 2 + (uint)values.Count * 12 + 4;
        using var external = new MemoryStream();
        foreach (var pair in values)
        {
            writer.Write(pair.Key); writer.Write((ushort)2); writer.Write((uint)pair.Value.Length);
            if (pair.Value.Length <= 4)
            {
                writer.Write(pair.Value); writer.Write(new byte[4 - pair.Value.Length]);
            }
            else
            {
                writer.Write(externalOffset + (uint)external.Length); external.Write(pair.Value);
            }
        }
        writer.Write(0u); writer.Write(external.ToArray()); writer.Flush();
        return stream.ToArray();
    }

    internal static byte[] AddExif(byte[] jpeg, byte[] tiff)
    {
        using var output = new MemoryStream();
        output.Write(jpeg.AsSpan(0, 2)); output.Write([0xff, 0xe1]);
        Span<byte> size = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)(tiff.Length + 8));
        output.Write(size); output.Write("Exif\0\0"u8); output.Write(tiff); output.Write(jpeg.AsSpan(2));
        return output.ToArray();
    }

    private static byte[] AddPngExif(byte[] png, byte[] tiff)
    {
        using var output = new MemoryStream();
        output.Write(png.AsSpan(0, 33));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)tiff.Length);
        output.Write(length); output.Write("eXIf"u8); output.Write(tiff);
        var crc = uint.MaxValue;
        foreach (var value in "eXIf"u8.ToArray().Concat(tiff))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        output.Write(length); output.Write(png.AsSpan(33));
        return output.ToArray();
    }

    private static byte[] AddWebpExif(byte[] webp, byte[] tiff, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write("RIFF"u8); output.Write(new byte[4]); output.Write("WEBP"u8);
        output.Write("VP8X"u8);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, 10); output.Write(length);
        Span<byte> extended = stackalloc byte[10];
        extended.Clear(); extended[0] = 0x08;
        extended[4] = (byte)(width - 1); extended[5] = (byte)((width - 1) >> 8); extended[6] = (byte)((width - 1) >> 16);
        extended[7] = (byte)(height - 1); extended[8] = (byte)((height - 1) >> 8); extended[9] = (byte)((height - 1) >> 16);
        output.Write(extended); output.Write(webp.AsSpan(12)); output.Write("EXIF"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)tiff.Length); output.Write(length); output.Write(tiff);
        if (tiff.Length % 2 != 0) output.WriteByte(0);
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        return result;
    }

    private sealed class NonSeekableInput(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
