using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Images;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Infrastructure.Configuration;

namespace Nexora.Worker.Images;

public sealed class ImageWorker(IServiceScopeFactory scopes, IOptions<ImageOptions> configured,
    TimeProvider clock, ILogger<ImageWorker> logger) : BackgroundService
{
    private readonly ImageOptions options = configured.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maintenance = MaintainLoopAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var work = await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().ClaimAsync(stoppingToken);
                    if (work is not null)
                    {
                        await ProcessAsync(work, scope.ServiceProvider, stoppingToken);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception failure)
                {
                    logger.LogWarning("Image polling failed with {FailureType}.", failure.GetType().Name);
                }
                await Task.Delay(options.PollInterval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await maintenance; }
    }

    private async Task ProcessAsync(ImageWorkItem work, IServiceProvider services, CancellationToken stoppingToken)
    {
        using var expiry = new CancellationTokenSource(Remaining(work.Lease.ExpiresAt), clock);
        using var timeout = new CancellationTokenSource(options.ProcessingTimeout, clock);
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, expiry.Token, timeout.Token);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewAsync(work.Lease, expiry, renewalStop.Token);
        Exception? failure = null;
        try
        {
            await services.GetRequiredService<IImageJobProcessor>().ProcessAsync(work, processing.Token);
            logger.LogInformation("Image job {JobId} for blob {BlobId} completed.", work.Lease.JobId, work.Blob.Id);
        }
        catch (Exception error) { failure = error; }
        finally { await renewalStop.CancelAsync(); await renewal; }
        if (failure is null || stoppingToken.IsCancellationRequested) return;
        if (expiry.IsCancellationRequested || failure is JobLeaseLostException)
        {
            logger.LogWarning("Image job {JobId} lost its lease.", work.Lease.JobId);
            return;
        }
        var (code, retryable) = failure switch
        {
            _ when timeout.IsCancellationRequested => ("image_processing_timeout", true),
            ImageProcessingException refused => (refused.Code, refused.Retryable),
            UnsafeStoragePathException => ("unsafe_storage_path", false),
            StorageIntegrityException => ("image_integrity_failed", false),
            FileNotFoundException or DirectoryNotFoundException => ("image_source_missing", false),
            IOException => ("storage_unavailable", true),
            _ => ("image_processing_failed", true)
        };
        logger.LogWarning("Image job {JobId} failed with {FailureCode}; retryable {Retryable}.", work.Lease.JobId, code, retryable);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().FailAsync(work.Lease, code, retryable, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning("Image failure persistence for job {JobId} failed with {FailureType}.", work.Lease.JobId, error.GetType().Name);
        }
    }

    private async Task RenewAsync(ImageJobLease lease, CancellationTokenSource expiry, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(options.LeaseRenewInterval, clock, stoppingToken);
                var startedAt = clock.GetUtcNow();
                await using var scope = scopes.CreateAsyncScope();
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, expiry.Token);
                if (!await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().RenewAsync(lease, stop.Token))
                {
                    await expiry.CancelAsync();
                    return;
                }
                expiry.CancelAfter(Remaining(startedAt + options.LeaseDuration));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception failure)
        {
            logger.LogWarning("Image lease renewal for job {JobId} failed with {FailureType}.", lease.JobId, failure.GetType().Name);
            await expiry.CancelAsync();
        }
    }

    private async Task MaintainLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<IImageWorkStore>();
                    await store.BackfillAsync(100, stoppingToken);
                    var files = scope.ServiceProvider.GetRequiredService<IDerivativeStorage>();
                    foreach (var item in await store.ListCleanupAsync(100, stoppingToken))
                    {
                        foreach (var attempt in item.Attempts)
                            await files.CleanAttemptAsync(attempt, attempt == item.PublishedGeneration, stoppingToken);
                        await store.FinishCleanupAsync(item.JobId, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception failure)
                {
                    logger.LogWarning("Image maintenance failed with {FailureType}.", failure.GetType().Name);
                }
                await Task.Delay(options.MaintenanceInterval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private TimeSpan Remaining(DateTimeOffset deadline) => deadline > clock.GetUtcNow() ? deadline - clock.GetUtcNow() : TimeSpan.Zero;
}
