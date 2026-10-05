using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Authentication;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class AuthenticationFlowTests
{
    [Fact]
    public async Task ProtectedEndpointsRejectAnonymousRequests()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/devices");

        await AuthenticationTestHost.AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidInputIsRejectedBeforeAnyDatabaseConnection()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            login = "", password = "", deviceName = "", platform = ""
        });
        using var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "" });

        await AuthenticationTestHost.AssertProblemAsync(login, HttpStatusCode.BadRequest, "invalid_request");
        await AuthenticationTestHost.AssertProblemAsync(refresh, HttpStatusCode.BadRequest, "invalid_request");
    }

    [PostgresFact]
    public async Task LoginIssuesOpaqueAccessAndStoresOnlyHashOf256BitRefreshToken()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        var tokens = await host.LoginAsync();

        Assert.True(!tokens.AccessToken.Contains('.'), "O access token deve ser opaco.");
        var decodedRefreshToken = WebEncoders.Base64UrlDecode(tokens.RefreshToken);
        Assert.Equal(32, decodedRefreshToken.Length);
        var expectedHash = SHA256.HashData(decodedRefreshToken);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var stored = await context.RefreshTokens.SingleAsync();
        Assert.Equal(32, stored.TokenHash.Length);
        Assert.True(stored.TokenHash.AsSpan().SequenceEqual(expectedHash), "O banco deve conter apenas SHA-256 do refresh token.");
        Assert.True(!context.Entry(stored).Properties.Any(property =>
            property.CurrentValue is string value && value == tokens.RefreshToken), "O token original não deve ser persistido.");
        using var devices = await host.GetDevicesAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, devices.StatusCode);
        var listed = await devices.Content.ReadFromJsonAsync<DeviceSummary[]>();
        Assert.NotNull(listed);
        Assert.Single(listed);
        Assert.Equal(tokens.DeviceId, listed[0].Id);
        Assert.DoesNotContain(AuthenticationTestHost.AdministratorPassword, host.Factory.CapturedLogs.Text);
        Assert.DoesNotContain(AuthenticationTestHost.DefaultDeviceName, host.Factory.CapturedLogs.Text);
        Assert.DoesNotContain(tokens.AccessToken, host.Factory.CapturedLogs.Text);
        Assert.DoesNotContain(tokens.RefreshToken, host.Factory.CapturedLogs.Text);
    }

    [PostgresFact]
    public async Task RefreshRotatesOnceAndReplayRevokesTheEntireSession()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        var original = await host.LoginAsync();
        var rotated = await host.RefreshAsync(original.RefreshToken);

        Assert.True(original.RefreshToken != rotated.RefreshToken, "Refresh deve produzir um novo token.");
        Assert.Equal(original.SessionId, rotated.SessionId);
        Assert.Equal(original.SessionExpiresAt.ToUnixTimeMilliseconds(), rotated.SessionExpiresAt.ToUnixTimeMilliseconds());
        using var beforeReplay = await host.GetDevicesAsync(rotated.AccessToken);
        Assert.Equal(HttpStatusCode.OK, beforeReplay.StatusCode);

        using var replay = await host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original.RefreshToken });
        await AuthenticationTestHost.AssertProblemAsync(replay, HttpStatusCode.Unauthorized, "invalid_refresh_token");
        using var originalAccess = await host.GetDevicesAsync(original.AccessToken);
        using var rotatedAccess = await host.GetDevicesAsync(rotated.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, originalAccess.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rotatedAccess.StatusCode);
        using var successor = await host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = rotated.RefreshToken });
        await AuthenticationTestHost.AssertProblemAsync(successor, HttpStatusCode.Unauthorized, "invalid_refresh_token");
    }

    [PostgresFact]
    public async Task ConcurrentRefreshAcceptsOnlyOneAndReplayInvalidatesItsSuccessor()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        var original = await host.LoginAsync();

        var responses = await Task.WhenAll(
            host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original.RefreshToken }),
            host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original.RefreshToken }));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
            var winner = await AuthenticationTestHost.ReadTokensAsync(responses.Single(response => response.StatusCode == HttpStatusCode.OK));
            using var access = await host.GetDevicesAsync(winner.AccessToken);
            Assert.Equal(HttpStatusCode.Unauthorized, access.StatusCode);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task LogoutRevokesAccessAndRefreshForThatSession()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        using var logout = await AuthenticationTestHost.SendAuthorizedAsync(host.Client, HttpMethod.Post,
            "/api/auth/logout", tokens.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var access = await host.GetDevicesAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, access.StatusCode);
        using var refresh = await host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        await AuthenticationTestHost.AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, "invalid_refresh_token");
    }

    [PostgresFact]
    public async Task ReusingADeviceRevokesItsPreviousSession()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        var original = await host.LoginAsync();
        var replacement = await host.LoginAsync(original.DeviceId);

        Assert.Equal(original.DeviceId, replacement.DeviceId);
        Assert.True(original.SessionId != replacement.SessionId);
        using var previous = await host.GetDevicesAsync(original.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, previous.StatusCode);
        using var previousRefresh = await host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original.RefreshToken });
        await AuthenticationTestHost.AssertProblemAsync(previousRefresh, HttpStatusCode.Unauthorized, "invalid_refresh_token");
        using var current = await host.GetDevicesAsync(replacement.AccessToken);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Single((await current.Content.ReadFromJsonAsync<DeviceSummary[]>())!);
    }

    [PostgresFact]
    public async Task DeviceRevocationImmediatelyInvalidatesAccessAndRefresh()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        using var revoke = await AuthenticationTestHost.SendAuthorizedAsync(host.Client, HttpMethod.Delete,
            $"/api/devices/{tokens.DeviceId}", tokens.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var access = await host.GetDevicesAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, access.StatusCode);
        using var refresh = await host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        await AuthenticationTestHost.AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, "invalid_refresh_token");
        using var reuse = await host.Client.PostAsJsonAsync("/api/auth/login", new
        {
            login = AuthenticationTestHost.AdministratorEmail,
            password = AuthenticationTestHost.AdministratorPassword,
            deviceName = "Replacement device",
            platform = "integration-tests",
            deviceId = tokens.DeviceId
        });
        await AuthenticationTestHost.AssertProblemAsync(reuse, HttpStatusCode.BadRequest, "invalid_device");
    }

    [PostgresFact]
    public async Task WrongPasswordAndUnknownAccountHaveTheSameGenericResponse()
    {
        await using var host = await AuthenticationTestHost.CreateAsync();
        foreach (var login in new[] { AuthenticationTestHost.AdministratorEmail, "unknown@nexora.test" })
        {
            using var response = await host.Client.PostAsJsonAsync("/api/auth/login", new
            {
                login, password = "Incorrect-private-password-721!", deviceName = "Private name", platform = "test"
            });
            await AuthenticationTestHost.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "invalid_credentials");
            Assert.DoesNotContain(login, await response.Content.ReadAsStringAsync());
        }

        Assert.DoesNotContain("Incorrect-private-password-721!", host.Factory.CapturedLogs.Text);
        Assert.DoesNotContain("Private name", host.Factory.CapturedLogs.Text);
    }

    [Fact]
    public async Task MalformedAndOversizePayloadsNeverExposeCredentials()
    {
        const string password = "Never-disclose-payload-password-86129!";
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var malformedBody = new StringContent(
            "{\"password\":\"" + password + "\",\"login\":\"administrator@nexora.test\"",
            System.Text.Encoding.UTF8, "application/json");
        using var malformed = await client.PostAsync("/api/auth/login", malformedBody);
        await AuthenticationTestHost.AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "invalid_request");
        Assert.DoesNotContain(password, await malformed.Content.ReadAsStringAsync());

        using var oversized = await client.PostAsJsonAsync("/api/auth/login", new
        {
            login = "administrator@nexora.test", password,
            deviceName = new string('x', 16 * 1024), platform = "test"
        });
        await AuthenticationTestHost.AssertProblemAsync(oversized, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
        Assert.DoesNotContain(password, await oversized.Content.ReadAsStringAsync());
        Assert.DoesNotContain(password, factory.CapturedLogs.Text);
    }
}
