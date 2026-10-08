using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Application.Uploads;
using Nexora.Infrastructure.Configuration;

namespace Nexora.Worker;

/// <summary>One assembly at a time, with independent scopes for renewals and maintenance.</summary>
public sealed class UploadWorker(IServiceScopeFactory scopes, IOptions<UploadOptions> configuredOptions,
    TimeProvider clock, ILogger<UploadWorker> logger) : BackgroundService
{
    private readonly UploadOptions options = configuredOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maintenance = RunMaintenanceLoopAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var workStore = scope.ServiceProvider.GetRequiredService<IUploadWorkStore>();
                    var work = await workStore.ClaimAsync(stoppingToken);
                    if (work is not null)
                    {
                        await ProcessClaimAsync(work, scope.ServiceProvider, stoppingToken);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception failure)
                {
                    logger.LogWarning("Upload worker polling failed with {FailureType}.", failure.GetType().Name);
                }
                await Task.Delay(options.PollInterval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Durable claims remain recoverable after their leases expire.
        }
        finally
        {
            await maintenance;
        }
    }

    private async Task ProcessClaimAsync(UploadWorkItem work, IServiceProvider services,
        CancellationToken stoppingToken)
    {
        var initialLeaseTime = work.Lease.ExpiresAt - clock.GetUtcNow();
        using var leaseExpiry = new CancellationTokenSource(Nonnegative(initialLeaseTime), clock);
        using var processingTimeout = new CancellationTokenSource(options.ProcessingTimeout, clock);
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken,
            leaseExpiry.Token, processingTimeout.Token);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewLeaseAsync(work.Lease, leaseExpiry, renewalStop.Token);
        Exception? operationFailure = null;
        try
        {
            var processor = services.GetRequiredService<IUploadJobProcessor>();
            await processor.ProcessAsync(work, processing.Token);
            logger.LogInformation("Upload job {JobId} for upload {UploadId} completed processing.",
                work.Lease.JobId, work.Lease.UploadId);
        }
        catch (Exception failure)
        {
            operationFailure = failure;
        }
        finally
        {
            await renewalStop.CancelAsync();
            await renewal;
        }
        if (operationFailure is null || stoppingToken.IsCancellationRequested)
        {
            return;
        }
        if (leaseExpiry.IsCancellationRequested || operationFailure is JobLeaseLostException)
        {
            logger.LogWarning("Upload job {JobId} lost its lease; processing stopped.", work.Lease.JobId);
            return;
        }
        var (code, retryable) = ClassifyFailure(operationFailure, processingTimeout.IsCancellationRequested);
        logger.LogWarning("Upload job {JobId} failed with {FailureCode}; retryable {Retryable}.",
            work.Lease.JobId, code, retryable);
        try
        {
            // Reporting uses a fresh context; it is fenced even if the lease expired meanwhile.
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IUploadWorkStore>();
            await store.FailAsync(work.Lease, code, retryable, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            logger.LogWarning("Upload job {JobId} failure recording failed with {FailureType}.",
                work.Lease.JobId, failure.GetType().Name);
        }
    }

    private async Task RenewLeaseAsync(JobLease lease, CancellationTokenSource leaseExpiry,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(options.LeaseRenewInterval, clock, stoppingToken);
                var renewalStartedAt = clock.GetUtcNow();
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IUploadWorkStore>();
                using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken, leaseExpiry.Token);
                if (!await store.RenewAsync(lease, renewalCancellation.Token))
                {
                    await leaseExpiry.CancelAsync();
                    return;
                }
                // Use the start of the call, so a slow response never overestimates DB expiry.
                leaseExpiry.CancelAfter(Nonnegative(renewalStartedAt + options.LeaseDuration - clock.GetUtcNow()));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            logger.LogWarning("Lease renewal for job {JobId} failed with {FailureType}.",
                lease.JobId, failure.GetType().Name);
            await leaseExpiry.CancelAsync();
        }
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunMaintenanceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception failure)
                {
                    logger.LogWarning("Upload maintenance failed with {FailureType}.", failure.GetType().Name);
                }
                await Task.Delay(options.MaintenanceInterval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUploadWorkStore>();
        var storage = scope.ServiceProvider.GetRequiredService<ITrackedTemporaryStorage>();
        await store.ExpireOpenAsync(cancellationToken);
        var cleanup = await store.ListCleanupAsync(100, cancellationToken);
        foreach (var item in cleanup)
        {
            try
            {
                foreach (var key in item.Keys)
                {
                    await storage.DeleteAttemptAsync(key, preserveCompleted: false, cancellationToken);
                }
                if (await store.FinishCleanupAsync(item.UploadId, cancellationToken))
                {
                    logger.LogInformation("Upload {UploadId} terminal files and reservation cleaned.", item.UploadId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                logger.LogWarning("Upload {UploadId} cleanup failed with {FailureType}.",
                    item.UploadId, failure.GetType().Name);
            }
        }
        var references = await store.ReferencedTemporaryIdsAsync(cancellationToken);
        var housekeeping = scope.ServiceProvider.GetRequiredService<IStorageHousekeeping>();
        await housekeeping.RemoveOrphansAsync(references, clock.GetUtcNow() - options.OrphanGracePeriod,
            cancellationToken);
    }

    private static TimeSpan Nonnegative(TimeSpan duration) => duration > TimeSpan.Zero ? duration : TimeSpan.Zero;

    private static (string Code, bool Retryable) ClassifyFailure(Exception failure, bool timedOut)
    {
        if (timedOut) return ("processing_timeout", true);
        return failure switch
        {
            StorageIntegrityException or StorageLimitExceededException => ("content_integrity", false),
            UnsafeStoragePathException => ("unsafe_storage_path", false),
            FileNotFoundException or DirectoryNotFoundException => ("content_missing", false),
            UploadOperationException { Code: "blob_unavailable" } => ("blob_unavailable", true),
            IOException => ("storage_unavailable", true),
            _ => ("processing_failed", true)
        };
    }
}
