using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexora.Application.Content;
using Nexora.Application.Images;
using Nexora.Infrastructure.Configuration;

namespace Nexora.Infrastructure.Images;

public sealed class ProcessImageRenderer(IOptions<ImageOptions> configured) : IImageRenderer
{
    public async Task<RenderedImage> RenderAsync(BlobDescriptor blob, Stream original, CancellationToken cancellationToken)
    {
        var options = configured.Value;
        if (blob.Size > options.MaximumInputBytes) throw new ImageProcessingException("image_input_too_large");
        var parameters = new ImageRenderParameters(blob.Size, blob.Sha256, blob.DetectedMimeType,
            options.MaximumInputBytes, options.MaximumPixels, options.MaximumDecodedBytes, options.MaximumDimension,
            options.ThumbnailSize, options.PreviewSize, options.MaximumDerivativeBytes);
        using var timeout = new CancellationTokenSource(options.ProcessingTimeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var process = new Process { StartInfo = StartInfo(options) };
        Task[]? running = null;
        try
        {
            if (!process.Start()) throw new ImageProcessingException("image_renderer_unavailable", true);
            var pump = PumpAsync(process.StandardInput.BaseStream, parameters, original, stop.Token);
            var read = ReadBoundedAsync(process.StandardOutput.BaseStream,
                checked(options.MaximumDerivativeBytes * 2 * 4 / 3 + 64 * 1024), stop.Token);
            var errors = ReadBoundedAsync(process.StandardError.BaseStream, 64 * 1024, stop.Token);
            var monitor = MonitorAsync(process, options.MaximumProcessMemoryBytes, stop.Token);
            running = [pump, read, errors, monitor];
            var pipes = new List<Task>(running);
            // A monitor or pipe failure must stop the process immediately; waiting
            // for every pipe first could leave a native decoder running indefinitely.
            while (pipes.Count > 0)
            {
                var completed = await Task.WhenAny(pipes);
                await completed;
                pipes.Remove(completed);
            }
            if (process.ExitCode != 0) throw new ImageProcessingException("image_renderer_failed", true);
            var response = JsonSerializer.Deserialize<ImageRenderResponse>(await read)
                ?? throw new ImageProcessingException("image_protocol_invalid", true);
            if (response.FailureCode is not null)
            {
                if (response.FailureCode.Length > 64 || response.FailureCode.Any(value => value is not (>= 'a' and <= 'z') and not '_'))
                    throw new ImageProcessingException("image_protocol_invalid", true);
                throw new ImageProcessingException(response.FailureCode, response.FailureCode == "image_renderer_unavailable");
            }
            var image = response.Image ?? throw new ImageProcessingException("image_protocol_invalid", true);
            if (image.Width <= 0 || image.Height <= 0 || image.Width > options.MaximumDimension || image.Height > options.MaximumDimension
                || (long)image.Width * image.Height > options.MaximumPixels
                || image.Thumbnail.Length > options.MaximumDerivativeBytes || image.Preview.Length > options.MaximumDerivativeBytes
                || image.CapturedAtLocal?.Kind is DateTimeKind.Local or DateTimeKind.Utc
                || (image.CapturedAtUtc is not null && image.CapturedAtUtc.Value.Offset != TimeSpan.Zero))
                throw new ImageProcessingException("image_protocol_invalid", true);
            return image;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ImageProcessingException("image_processing_timeout", true);
        }
        catch (JsonException)
        {
            throw new ImageProcessingException("image_protocol_invalid", true);
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                if (process.Id > 0 && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(exit.Token);
                }
            }
            catch (InvalidOperationException) { } // Start failed; there is no child.
            if (running is not null)
            {
                try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { } // Preserve the original failure after closing the child's pipes.
            }
        }
    }

    private static ProcessStartInfo StartInfo(ImageOptions options)
    {
        var executable = Environment.ProcessPath!;
        var self = Path.GetFileNameWithoutExtension(executable).Equals("Nexora.Worker", StringComparison.OrdinalIgnoreCase);
        if (!self && !Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            executable = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (!self) info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Nexora.Worker.dll"));
        info.ArgumentList.Add("render-image");
        // Never pass connection strings, certificates, storage paths or the
        // parent's configuration/secrets to the decoder's environment.
        info.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["DOTNET_EnableDiagnostics"] = "0";
        info.Environment["DOTNET_GCHeapHardLimit"] = (options.MaximumProcessMemoryBytes / 2).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["TZ"] = "UTC";
        return info;
    }

    private static async Task PumpAsync(Stream pipe, ImageRenderParameters parameters, Stream original, CancellationToken cancellationToken)
    {
        try
        {
            await JsonSerializer.SerializeAsync(pipe, parameters, cancellationToken: cancellationToken);
            await pipe.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
            var remaining = parameters.ExpectedLength;
            var buffer = new byte[64 * 1024];
            while (remaining > 0)
            {
                var read = await original.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new ImageProcessingException("image_integrity_failed");
                await pipe.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
            if (await original.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
                throw new ImageProcessingException("image_integrity_failed");
            await pipe.FlushAsync(cancellationToken);
        }
        finally { await pipe.DisposeAsync(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream pipe, int maximumLength, CancellationToken cancellationToken)
    {
        using var data = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await pipe.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (data.Length + read > maximumLength) throw new ImageProcessingException("image_protocol_limit", true);
            data.Write(buffer, 0, read);
        }
        return data.ToArray();
    }

    private static async Task MonitorAsync(Process process, long maximumMemory, CancellationToken cancellationToken)
    {
        while (!process.HasExited)
        {
            process.Refresh();
            if (process.WorkingSet64 > maximumMemory) throw new ImageProcessingException("image_memory_limit_exceeded");
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        await process.WaitForExitAsync(cancellationToken);
    }
}
