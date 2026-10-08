using System.Security.Cryptography;
using Nexora.Application.Images;
using SkiaSharp;

namespace Nexora.Infrastructure.Images;

public sealed class SkiaImageRenderer
{
    public RenderedImage Render(Stream original, ImageRenderParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateParameters(parameters);
        if (!original.CanRead || !original.CanSeek) throw new ImageProcessingException("image_invalid");
        ValidateContent(original, parameters, cancellationToken);
        ImageContainerValidator.Validate(original, parameters.ExpectedMimeType, cancellationToken);

        try
        {
            original.Position = 0;
            using var managedStream = new SKManagedStream(original, false);
            using var codec = SKCodec.Create(managedStream, out var creationResult);
            if (codec is null || creationResult != SKCodecResult.Success)
                throw new ImageProcessingException("image_invalid");
            ValidateFormat(codec, parameters.ExpectedMimeType);
            var encodedWidth = codec.Info.Width;
            var encodedHeight = codec.Info.Height;
            if (encodedWidth <= 0 || encodedHeight <= 0 || encodedWidth > parameters.MaximumDimension
                || encodedHeight > parameters.MaximumDimension)
                throw new ImageProcessingException("image_pixel_limit_exceeded");
            var pixels = (long)encodedWidth * encodedHeight;
            if (pixels > parameters.MaximumPixels)
                throw new ImageProcessingException("image_pixel_limit_exceeded");

            var orientation = codec.EncodedOrigin;
            var swapsDimensions = orientation is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var width = swapsDimensions ? encodedHeight : encodedWidth;
            var height = swapsDimensions ? encodedWidth : encodedHeight;
            var largest = OutputSize(width, height, Math.Max(parameters.ThumbnailSize, parameters.PreviewSize));
            // One source plus one derivative bitmap; orientation is applied directly on the small target.
            if (pixels * 4 + (long)largest.Width * largest.Height * 4 > parameters.MaximumDecodedBytes)
                throw new ImageProcessingException("image_decoded_limit_exceeded");

            cancellationToken.ThrowIfCancellationRequested();
            using var colorSpace = SKColorSpace.CreateSrgb();
            var imageInfo = new SKImageInfo(encodedWidth, encodedHeight, SKColorType.Rgba8888,
                SKAlphaType.Premul, colorSpace);
            using var bitmap = new SKBitmap();
            if (!bitmap.TryAllocPixels(imageInfo)) throw new ImageProcessingException("image_memory_limit_exceeded");
            if (codec.GetPixels(imageInfo, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new ImageProcessingException("image_invalid");
            cancellationToken.ThrowIfCancellationRequested();
            bitmap.SetImmutable();
            using var image = SKImage.FromBitmap(bitmap);
            var thumbnail = EncodeDerivative(image, orientation, width, height, parameters.ThumbnailSize,
                parameters.MaximumDerivativeBytes, colorSpace, cancellationToken);
            var preview = EncodeDerivative(image, orientation, width, height, parameters.PreviewSize,
                parameters.MaximumDerivativeBytes, colorSpace, cancellationToken);
            var captured = ImageCaptureMetadataReader.Read(original, parameters.ExpectedMimeType, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new RenderedImage(width, height, captured.Local, captured.Utc, thumbnail, preview);
        }
        catch (OutOfMemoryException)
        {
            throw new ImageProcessingException("image_memory_limit_exceeded");
        }
    }

    private static void ValidateParameters(ImageRenderParameters parameters)
    {
        if (parameters.MaximumInputBytes <= 0 || parameters.MaximumPixels <= 0 || parameters.MaximumDecodedBytes <= 0
            || parameters.MaximumDimension <= 0 || parameters.MaximumDimension > 32_768
            || parameters.ThumbnailSize is < 1 or > 4_096 || parameters.PreviewSize is < 1 or > 4_096
            || parameters.MaximumDerivativeBytes <= 0)
            throw new ArgumentException("Invalid image rendering limits.", nameof(parameters));
        if (parameters.ExpectedLength <= 0 || parameters.ExpectedLength > parameters.MaximumInputBytes)
            throw new ImageProcessingException("image_input_too_large");
        if (parameters.ExpectedSha256 is not { Length: 64 }
            || parameters.ExpectedSha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ImageProcessingException("image_integrity_failed");
    }

    private static void ValidateContent(Stream original, ImageRenderParameters parameters, CancellationToken cancellationToken)
    {
        if (original.Length > parameters.MaximumInputBytes) throw new ImageProcessingException("image_input_too_large");
        if (original.Length != parameters.ExpectedLength) throw new ImageProcessingException("image_integrity_failed");
        original.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = original.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length += read;
            if (length > parameters.MaximumInputBytes) throw new ImageProcessingException("image_input_too_large");
            hash.AppendData(buffer.AsSpan(0, read));
        }
        var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (length != parameters.ExpectedLength || actualHash != parameters.ExpectedSha256)
            throw new ImageProcessingException("image_integrity_failed");
    }

    private static void ValidateFormat(SKCodec codec, string mimeType)
    {
        var matching = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => mimeType == "image/jpeg",
            SKEncodedImageFormat.Png => mimeType == "image/png",
            SKEncodedImageFormat.Webp => mimeType == "image/webp",
            _ => false
        };
        if (!matching) throw new ImageProcessingException("image_format_unsupported");
        if (codec.FrameCount > 1) throw new ImageProcessingException("image_animation_unsupported");
    }

    private static byte[] EncodeDerivative(SKImage image, SKEncodedOrigin orientation, int width, int height,
        int maximumSide, int maximumBytes, SKColorSpace colorSpace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = OutputSize(width, height, maximumSide);
        var info = new SKImageInfo(output.Width, output.Height, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace);
        using var bitmap = new SKBitmap();
        if (!bitmap.TryAllocPixels(info)) throw new ImageProcessingException("image_memory_limit_exceeded");
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var matrix = OrientedMatrix(orientation, image.Width, image.Height,
                (float)output.Width / width, (float)output.Height / height);
            canvas.SetMatrix(in matrix);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            canvas.Flush();
        }
        cancellationToken.ThrowIfCancellationRequested();
        bitmap.SetImmutable();
        using var derivative = SKImage.FromBitmap(bitmap);
        using var encoded = derivative.Encode(SKEncodedImageFormat.Png, 100);
        if (encoded is null || encoded.Size <= 0) throw new ImageProcessingException("image_invalid");
        if (encoded.Size > maximumBytes) throw new ImageProcessingException("image_derivative_too_large");
        cancellationToken.ThrowIfCancellationRequested();
        return encoded.ToArray();
    }

    private static (int Width, int Height) OutputSize(int width, int height, int maximumSide)
    {
        var scale = Math.Min(1d, (double)maximumSide / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
    }

    private static SKMatrix OrientedMatrix(SKEncodedOrigin orientation, int width, int height, float sx, float sy) =>
        orientation switch
        {
            SKEncodedOrigin.TopLeft => new SKMatrix(sx, 0, 0, 0, sy, 0, 0, 0, 1),
            SKEncodedOrigin.TopRight => new SKMatrix(-sx, 0, width * sx, 0, sy, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-sx, 0, width * sx, 0, -sy, height * sy, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(sx, 0, 0, 0, -sy, height * sy, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, sx, 0, sy, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -sx, height * sx, sy, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -sx, height * sx, -sy, 0, width * sy, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, sx, 0, -sy, 0, width * sy, 0, 0, 1),
            _ => throw new ImageProcessingException("image_invalid")
        };
}
