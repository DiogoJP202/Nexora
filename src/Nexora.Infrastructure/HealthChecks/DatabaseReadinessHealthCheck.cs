using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.HealthChecks;

public sealed class DatabaseReadinessHealthCheck(
    NexoraDbContext dbContext,
    ILogger<DatabaseReadinessHealthCheck> logger) : IHealthCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly EventId CheckFailed = new(1001, "DatabaseReadinessFailed");
    private static readonly EventId MigrationsPending = new(1002, "DatabaseMigrationsPending");

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            if (!await dbContext.Database.CanConnectAsync(timeout.Token))
            {
                logger.LogWarning(CheckFailed, "Database readiness check could not establish a connection.");
                return HealthCheckResult.Unhealthy("Database is not ready.");
            }

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(timeout.Token);

            if (pendingMigrations.Any())
            {
                logger.LogWarning(MigrationsPending, "Database readiness check found pending migrations.");
                return HealthCheckResult.Unhealthy("Database is not ready.");
            }

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(CheckFailed, "Database readiness check timed out or was cancelled.");
            return HealthCheckResult.Unhealthy("Database is not ready.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(CheckFailed, "Database readiness check failed with {FailureType}.", exception.GetType().Name);
            return HealthCheckResult.Unhealthy("Database is not ready.");
        }
    }
}
