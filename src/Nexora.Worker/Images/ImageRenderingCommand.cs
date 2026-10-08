using System.Text.Encodings.Web;
using System.Text.Json;
using Nexora.Application.Images;
using Nexora.Infrastructure.Images;

namespace Nexora.Worker.Images;

internal static class ImageRenderingCommand
{
    internal static async Task<int> RunAsync()
    {
        // This entry point creates no host, database connection, storage adapter
        // or log provider. Its only input/output is the bounded pipe protocol.
        var input = Console.OpenStandardInput();
        ImageRenderResponse response;
        try
        {
            using var header = new MemoryStream();
            var one = new byte[1];
            while (await input.ReadAsync(one) != 0 && one[0] != '\n')
            {
                if (header.Length >= 4096) throw new ImageProcessingException("image_protocol_invalid");
                header.WriteByte(one[0]);
            }
            var parameters = JsonSerializer.Deserialize<ImageRenderParameters>(header.ToArray())
                ?? throw new ImageProcessingException("image_protocol_invalid");
            if (parameters.ExpectedLength < 0 || parameters.ExpectedLength > parameters.MaximumInputBytes
                || parameters.MaximumInputBytes > 256 * 1024 * 1024 || parameters.MaximumInputBytes <= 0)
                throw new ImageProcessingException("image_input_too_large");
            using var original = new MemoryStream((int)parameters.ExpectedLength);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer)) != 0)
            {
                if (original.Length + read > parameters.ExpectedLength)
                    throw new ImageProcessingException("image_integrity_failed");
                original.Write(buffer, 0, read);
            }
            if (original.Length != parameters.ExpectedLength) throw new ImageProcessingException("image_integrity_failed");
            original.Position = 0;
            var image = new SkiaImageRenderer().Render(original, parameters, CancellationToken.None);
            response = new ImageRenderResponse(image, null);
        }
        catch (ImageProcessingException failure)
        {
            response = new ImageRenderResponse(null, failure.Code);
        }
        catch (OutOfMemoryException)
        {
            response = new ImageRenderResponse(null, "image_memory_limit_exceeded");
        }
        catch (Exception)
        {
            response = new ImageRenderResponse(null, "image_renderer_unavailable");
        }
        await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), response,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return 0;
    }
}
