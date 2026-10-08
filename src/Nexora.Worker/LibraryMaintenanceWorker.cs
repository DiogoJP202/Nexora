using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Content;
using Nexora.Infrastructure.Configuration;

namespace Nexora.Worker;

public sealed class LibraryMaintenanceWorker(IServiceScopeFactory scopes, IOptions<LibraryOptions> configured,
    TimeProvider clock, ILogger<LibraryMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configured.Value;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var maintenance = scope.ServiceProvider.GetRequiredService<IContentMaintenance>();
                    await maintenance.PurgeExpiredAssetsAsync(options.MaintenanceBatchSize, stoppingToken);
                    await maintenance.CollectBlobsAsync(options.MaintenanceBatchSize, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception failure)
                {
                    logger.LogWarning("Library maintenance failed with {FailureType}.", failure.GetType().Name);
                }
                await Task.Delay(options.MaintenanceInterval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
