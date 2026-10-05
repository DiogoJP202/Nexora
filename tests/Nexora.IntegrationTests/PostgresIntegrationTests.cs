using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class PostgresIntegrationTests
{
    [PostgresFact]
    public async Task InitialMigrationAppliesToEmptyDatabaseAndMatchesTheCurrentModel()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var scope = database.CreateContextScope();
        var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();

        Assert.Empty(await database.ReadPublicTablesAsync());
        Assert.NotEmpty(await context.Database.GetPendingMigrationsAsync());

        await context.Database.MigrateAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        var tables = await database.ReadPublicTablesAsync();
        foreach (var table in new[]
                 {
                     "AspNetUsers", "AspNetRoles", "AspNetUserClaims", "AspNetRoleClaims",
                     "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "AspNetUserPasskeys",
                     "__EFMigrationsHistory"
                 })
        {
            Assert.Contains(table, tables);
        }
    }

    [PostgresFact]
    public async Task ApiDoesNotApplyMigrationsAndReadinessChangesAfterExplicitMigration()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = new ApiFactory(configuration: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] = database.ConnectionString
        });
        using var client = factory.CreateClient();

        using var liveness = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);

        using var beforeMigration = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, beforeMigration.StatusCode);
        await HealthAndConfigurationTests.AssertHealthResponseAsync(beforeMigration, "Unhealthy");
        Assert.Empty(await database.ReadPublicTablesAsync());

        await using (var scope = database.CreateContextScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            await context.Database.MigrateAsync();
        }

        using var afterMigration = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, afterMigration.StatusCode);
        await HealthAndConfigurationTests.AssertHealthResponseAsync(afterMigration, "Healthy");
    }
}
