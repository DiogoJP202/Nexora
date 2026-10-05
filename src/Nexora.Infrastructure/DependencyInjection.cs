using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.HealthChecks;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();

        services.AddDbContext<NexoraDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseNpgsql(database.Nexora)
                .EnableSensitiveDataLogging(false)
                .EnableDetailedErrors(false)
                // Readiness emits sanitized failures instead of provider exceptions containing database details.
                .ConfigureWarnings(warnings => warnings.Ignore(
                    RelationalEventId.ConnectionError,
                    RelationalEventId.CommandError,
                    CoreEventId.QueryIterationFailed,
                    CoreEventId.SaveChangesFailed));
        });

        services.AddIdentityCore<NexoraUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<NexoraDbContext>();

        services.AddHealthChecks().AddCheck<DatabaseReadinessHealthCheck>(
            "database",
            tags: ["ready"],
            timeout: DatabaseReadinessHealthCheck.Timeout);

        return services;
    }
}
