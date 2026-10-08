using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nexora.Application.Authentication;
using Nexora.Application.Content;
using Nexora.Application.Jobs;
using Nexora.Application.Images;
using Nexora.Application.Storage;
using Nexora.Application.Uploads;
using Nexora.Infrastructure.Authentication;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Content;
using Nexora.Infrastructure.HealthChecks;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Images;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Storage;
using Nexora.Infrastructure.Uploads;

namespace Nexora.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<UploadOptions>, UploadOptionsValidator>();
        services.AddOptions<UploadOptions>().Bind(configuration.GetSection(UploadOptions.SectionName)).ValidateOnStart();

        services.AddSingleton<IValidateOptions<ImageOptions>, ImageOptionsValidator>();
        services.AddOptions<ImageOptions>().Bind(configuration.GetSection(ImageOptions.SectionName)).ValidateOnStart();

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

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddAuthentication(AuthenticationConstants.Scheme).AddBearerToken(AuthenticationConstants.Scheme);
        services.AddOptions<BearerTokenOptions>(AuthenticationConstants.Scheme).Configure<TimeProvider>((options, clock) =>
        {
            options.TimeProvider = clock;
            options.BearerTokenExpiration = AuthenticationConstants.AccessTokenLifetime;
        });

        services.AddIdentityCore<NexoraUser>(options =>
            {
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 14;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<NexoraDbContext>()
            .AddSignInManager();

        services.AddSingleton<DummyPasswordVerifier>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<IAccountAdministration, AccountAdministrationService>();
        services.AddSingleton<LocalFileBlobStorage>();
        services.AddSingleton<IBlobStorage>(provider => provider.GetRequiredService<LocalFileBlobStorage>());
        services.AddSingleton<ITrackedBlobStorage>(provider => provider.GetRequiredService<LocalFileBlobStorage>());
        services.AddSingleton<LocalFileTemporaryStorage>();
        services.AddSingleton<ITemporaryStorage>(provider => provider.GetRequiredService<LocalFileTemporaryStorage>());
        services.AddSingleton<ITrackedTemporaryStorage>(provider => provider.GetRequiredService<LocalFileTemporaryStorage>());
        services.AddScoped<IContentCatalog, PostgresContentCatalog>();
        services.AddScoped<IAssetIngestionService, AssetIngestionService>();
        services.AddScoped<IUploadContentCatalog, PostgresContentCatalog>();
        services.AddScoped<IStagedAssetIngestionService, StagedAssetIngestionService>();
        services.AddScoped<IUploadService, PostgresUploadService>();
        services.AddScoped<IUploadWorkStore, PostgresUploadWorkStore>();
        services.AddScoped<IUploadJobProcessor, UploadJobProcessor>();
        services.AddSingleton<IDerivativeStorage, LocalDerivativeStorage>();
        services.AddSingleton<IImageRenderer, ProcessImageRenderer>();
        services.AddScoped<IImageWorkStore, PostgresImageWorkStore>();
        services.AddScoped<IImageJobProcessor, ImageJobProcessor>();
        services.AddScoped<IAssetDerivatives, PostgresAssetDerivatives>();
        services.AddScoped<IAssetLibrary, PostgresAssetLibrary>();
        services.AddScoped<IStorageStatusService, PostgresStorageStatusService>();
        services.AddSingleton<IStorageUsageReader, LocalStorageUsageReader>();
        services.AddSingleton<IStorageHousekeeping, LocalStorageHousekeeping>();

        services.AddHealthChecks().AddCheck<DatabaseReadinessHealthCheck>(
            "database",
            tags: ["ready"],
            timeout: DatabaseReadinessHealthCheck.Timeout);

        return services;
    }
}
