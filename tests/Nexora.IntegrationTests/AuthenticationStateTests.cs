using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Application.Authentication;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class AuthenticationStateTests
{
    [PostgresFact]
    public async Task AccessExpiresAfterFiveMinutesAndRefreshKeepsTheAbsoluteSessionExpiry()
    {
        var clock = new MutableTimeProvider();
        await using var host = await AuthenticationTestHost.CreateAsync(clock);
        var original = await host.LoginAsync();
        clock.Advance(TimeSpan.FromMinutes(6));

        using var expiredAccess = await host.GetDevicesAsync(original.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredAccess.StatusCode);
        var refreshed = await host.RefreshAsync(original.RefreshToken);
        Assert.Equal(original.SessionExpiresAt.ToUnixTimeMilliseconds(), refreshed.SessionExpiresAt.ToUnixTimeMilliseconds());
        using var refreshedAccess = await host.GetDevicesAsync(refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.OK, refreshedAccess.StatusCode);

        clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(7));
        var nearExpiry = await host.RefreshAsync(refreshed.RefreshToken);
        // Give a correctly protected ticket more time than the session, so the next
        // request tests the database session check independently of bearer expiry.
        var protector = host.Factory.Services.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>()
            .Get(AuthenticationConstants.Scheme).BearerTokenProtector;
        var ticket = protector.Unprotect(nearExpiry.AccessToken);
        Assert.NotNull(ticket);
        ticket.Properties.ExpiresUtc = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        var accessBeyondSession = protector.Protect(ticket);
        clock.Advance(TimeSpan.FromMinutes(2));
        using var expiredSession = await host.GetDevicesAsync(accessBeyondSession);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredSession.StatusCode);
        using var expiredRefresh = await host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = nearExpiry.RefreshToken });
        await AuthenticationTestHost.AssertProblemAsync(expiredRefresh, HttpStatusCode.Unauthorized, "invalid_refresh_token");
    }

    [PostgresFact]
    public async Task AuthenticatedActivityThrottlesDeviceLastSeenUpdates()
    {
        var clock = new MutableTimeProvider();
        await using var host = await AuthenticationTestHost.CreateAsync(clock);
        var tokens = await host.LoginAsync();
        clock.Advance(TimeSpan.FromMinutes(2));

        using var earlyActivity = await host.GetDevicesAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, earlyActivity.StatusCode);
        var earlyDevices = await earlyActivity.Content.ReadFromJsonAsync<DeviceSummary[]>();
        Assert.NotNull(earlyDevices);
        var earlyDevice = Assert.Single(earlyDevices);
        Assert.Equal(earlyDevice.CreatedAt, earlyDevice.LastSeenAt);

        clock.Advance(TimeSpan.FromMinutes(4));
        var refreshed = await host.RefreshAsync(tokens.RefreshToken);
        using var laterActivity = await host.GetDevicesAsync(refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.OK, laterActivity.StatusCode);
        var laterDevices = await laterActivity.Content.ReadFromJsonAsync<DeviceSummary[]>();
        Assert.NotNull(laterDevices);
        var device = Assert.Single(laterDevices);
        Assert.Equal(clock.GetUtcNow(), device.LastSeenAt);
        Assert.True(device.LastSeenAt > device.CreatedAt);
    }

    [PostgresFact]
    public async Task AnotherOwnersDeviceCannotBeListedRevokedOrReused()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        var administrator = await host.LoginAsync();
        const string secondEmail = "second-owner@nexora.test";
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<NexoraUser>>();
            var result = await manager.CreateAsync(new NexoraUser
            {
                Id = Guid.NewGuid(), UserName = secondEmail, Email = secondEmail, EmailConfirmed = true
            }, AuthenticationTestHost.AdministratorPassword);
            Assert.True(result.Succeeded);
        }

        var secondOwner = await host.LoginAsync(email: secondEmail);
        using var administratorList = await host.GetDevicesAsync(administrator.AccessToken);
        var administratorDevices = await administratorList.Content.ReadFromJsonAsync<DeviceSummary[]>();
        Assert.NotNull(administratorDevices);
        Assert.Equal(administrator.DeviceId, Assert.Single(administratorDevices).Id);
        using var secondOwnerList = await host.GetDevicesAsync(secondOwner.AccessToken);
        var secondOwnerDevices = await secondOwnerList.Content.ReadFromJsonAsync<DeviceSummary[]>();
        Assert.NotNull(secondOwnerDevices);
        Assert.Equal(secondOwner.DeviceId, Assert.Single(secondOwnerDevices).Id);

        foreach (var deviceId in new[] { secondOwner.DeviceId, Guid.NewGuid() })
        {
            using var revoke = await AuthenticationTestHost.SendAuthorizedAsync(host.Client, HttpMethod.Delete,
                $"/api/devices/{deviceId}", administrator.AccessToken);
            await AuthenticationTestHost.AssertProblemAsync(revoke, HttpStatusCode.NotFound, "resource_not_found");
        }

        using var reuse = await host.Client.PostAsJsonAsync("/api/auth/login", new
        {
            login = AuthenticationTestHost.AdministratorEmail,
            password = AuthenticationTestHost.AdministratorPassword,
            deviceName = "Requested device", platform = "test", deviceId = secondOwner.DeviceId
        });
        await AuthenticationTestHost.AssertProblemAsync(reuse, HttpStatusCode.BadRequest, "invalid_device");
        using var unaffectedOwner = await host.GetDevicesAsync(secondOwner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, unaffectedOwner.StatusCode);
    }

    [PostgresFact]
    public async Task SecurityStampChangeInvalidatesAnOtherwiseValidAccessToken()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<NexoraUser>>();
            var user = await manager.FindByIdAsync(host.AdministratorId.ToString());
            Assert.NotNull(user);
            Assert.True((await manager.UpdateSecurityStampAsync(user)).Succeeded);
        }

        using var access = await host.GetDevicesAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, access.StatusCode);
        using var refresh = await host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        await AuthenticationTestHost.AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, "invalid_refresh_token");
    }

    [PostgresFact]
    public async Task LockoutInvalidatesSessionsAndLocalPasswordResetUnlocksAndRevokesThem()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        for (var failure = 0; failure < 5; failure++)
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var authentication = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
            var result = await authentication.LoginAsync(new LoginCommand(AuthenticationTestHost.AdministratorEmail,
                "Invalid-password-624!", "Private test name", "test"), CancellationToken.None);
            Assert.Equal(AuthenticationFailure.InvalidCredentials, result.Failure);
        }

        using var lockedAccess = await host.GetDevicesAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, lockedAccess.StatusCode);
        using var lockedLogin = await host.Client.PostAsJsonAsync("/api/auth/login", new
        {
            login = AuthenticationTestHost.AdministratorEmail, password = AuthenticationTestHost.AdministratorPassword,
            deviceName = "Private test name", platform = "test"
        });
        await AuthenticationTestHost.AssertProblemAsync(lockedLogin, HttpStatusCode.Unauthorized, "invalid_credentials");

        const string replacementPassword = "Replacement-test-password-6482!";
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var administration = scope.ServiceProvider.GetRequiredService<IAccountAdministration>();
            var result = await administration.ResetAdministratorPasswordAsync(replacementPassword, CancellationToken.None);
            Assert.True(result.Succeeded);
            var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var user = await context.Users.SingleAsync();
            Assert.Equal(0, user.AccessFailedCount);
            Assert.True(user.LockoutEnd is null || user.LockoutEnd <= DateTimeOffset.UtcNow);
        }

        using var oldAccess = await host.GetDevicesAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldAccess.StatusCode);
        using var oldRefresh = await host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        await AuthenticationTestHost.AssertProblemAsync(oldRefresh, HttpStatusCode.Unauthorized, "invalid_refresh_token");
        var replacement = await host.LoginAsync(password: replacementPassword);
        using var newAccess = await host.GetDevicesAsync(replacement.AccessToken);
        Assert.Equal(HttpStatusCode.OK, newAccess.StatusCode);
        Assert.DoesNotContain(replacementPassword, host.Factory.CapturedLogs.Text);
    }

    [Fact]
    public async Task LoginAndRefreshRateLimitsRejectRequestsBeyondTheirBudgets()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var login = await client.PostAsJsonAsync("/api/auth/login", new
            {
                login = "", password = "", deviceName = "", platform = ""
            });
            Assert.Equal(attempt < 5 ? HttpStatusCode.BadRequest : HttpStatusCode.TooManyRequests, login.StatusCode);
        }

        for (var attempt = 0; attempt < 31; attempt++)
        {
            using var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "" });
            Assert.Equal(attempt < 30 ? HttpStatusCode.BadRequest : HttpStatusCode.TooManyRequests, refresh.StatusCode);
        }
    }
}
