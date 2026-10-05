using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Authentication;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class AccountAdministrationTests
{
    [PostgresFact]
    public async Task RepeatedBootstrapPreservesTheExistingAdministrator()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<IAccountAdministration>();

        var result = await administration.BootstrapAsync("another@nexora.test", "Different-test-password-123!", CancellationToken.None);

        Assert.Equal(AuthenticationFailure.AlreadyInitialized, result.Failure);
        var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(host.AdministratorId, (await context.Users.SingleAsync()).Id);
        _ = await host.LoginAsync();
    }

    [PostgresFact]
    public async Task ConcurrentBootstrapCreatesExactlyOneAdministrator()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var migrationScope = database.CreateContextScope())
        {
            await migrationScope.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.MigrateAsync();
        }

        await using var firstScope = database.CreateContextScope();
        await using var secondScope = database.CreateContextScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IAccountAdministration>();
        var second = secondScope.ServiceProvider.GetRequiredService<IAccountAdministration>();
        var results = await Task.WhenAll(
            first.BootstrapAsync("first@nexora.test", AuthenticationTestHost.AdministratorPassword, CancellationToken.None),
            second.BootstrapAsync("second@nexora.test", AuthenticationTestHost.AdministratorPassword, CancellationToken.None));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => result.Failure == AuthenticationFailure.AlreadyInitialized);
        await using var inspectionScope = database.CreateContextScope();
        Assert.Equal(1, await inspectionScope.ServiceProvider.GetRequiredService<NexoraDbContext>().Users.CountAsync());
    }

    [PostgresFact]
    public async Task RejectedBootstrapPasswordLeavesTheDatabaseUninitialized()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var scope = database.CreateContextScope();
        var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        await context.Database.MigrateAsync();
        var administration = scope.ServiceProvider.GetRequiredService<IAccountAdministration>();

        var rejected = await administration.BootstrapAsync("administrator@nexora.test", "weak", CancellationToken.None);

        Assert.Equal(AuthenticationFailure.PasswordRejected, rejected.Failure);
        Assert.Equal(0, await context.Users.CountAsync());
        var accepted = await administration.BootstrapAsync("administrator@nexora.test", AuthenticationTestHost.AdministratorPassword,
            CancellationToken.None);
        Assert.True(accepted.Succeeded);
    }
}
