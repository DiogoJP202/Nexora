using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexora.Infrastructure;
using Nexora.Worker.Images;

namespace Nexora.Worker;

internal static class WorkerProgram
{
    public static async Task Main(string[] args)
    {
        if (args is ["render-image"])
        {
            Environment.ExitCode = await ImageRenderingCommand.RunAsync();
            return;
        }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        });
        builder.Configuration.AddEnvironmentVariables(prefix: "NEXORA_");
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddHostedService<UploadWorker>();
        builder.Services.AddHostedService<ImageWorker>();
        using var host = builder.Build();
        await host.RunAsync();
    }
}
