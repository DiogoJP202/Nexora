using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Nexora.IntegrationTests;

public sealed class EnvironmentConfigurationTests
{
    [Fact]
    public async Task NexoraPrefixedEnvironmentOverridesConfigurationInAnIsolatedProcess()
    {
        var apiAssembly = typeof(global::Program).Assembly.Location;
        Assert.True(File.Exists(Path.ChangeExtension(apiAssembly, ".runtimeconfig.json")),
            "O runtimeconfig da API precisa ser copiado ao output dos testes.");

        var port = FindAvailableLoopbackPort();
        using var protectionFiles = new DataProtectionTestFiles();
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = Path.GetDirectoryName(apiAssembly)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(apiAssembly);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{port}");

        // Configuration lives only in this child process; parallel tests keep their environment.
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("NEXORA_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["Storage__RootPath"] = "invalid-relative-storage";
        startInfo.Environment["ConnectionStrings__Nexora"] = "Host=127.0.0.1";
        startInfo.Environment["NEXORA_Storage__RootPath"] = Path.Combine(Path.GetTempPath(), "nexora-env-tests");
        startInfo.Environment["NEXORA_ConnectionStrings__Nexora"] = ApiFactory.UnreachableConnectionString;
        startInfo.Environment["NEXORA_DataProtection__KeyDirectory"] = protectionFiles.KeyDirectory;
        startInfo.Environment["NEXORA_DataProtection__CertificatePath"] = protectionFiles.CertificatePath;

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(1)
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var healthy = false;
            while (!deadline.IsCancellationRequested && !process.HasExited)
            {
                try
                {
                    using var response = await client.GetAsync("/health/live", deadline.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        await HealthAndConfigurationTests.AssertHealthResponseAsync(response, "Healthy");
                        healthy = true;
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    // Kestrel has not bound the port yet.
                }
                catch (OperationCanceledException)
                {
                    // The bounded per-request timeout or startup deadline expired.
                }

                if (!deadline.IsCancellationRequested)
                {
                    await Task.Delay(50);
                }
            }

            Assert.True(healthy,
                "A API não iniciou com NEXORA_Storage__RootPath e NEXORA_ConnectionStrings__Nexora dentro de 10 segundos.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var shutdownDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(shutdownDeadline.Token);
            await Task.WhenAll(standardOutput, standardError).WaitAsync(shutdownDeadline.Token);
        }
    }

    private static int FindAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
