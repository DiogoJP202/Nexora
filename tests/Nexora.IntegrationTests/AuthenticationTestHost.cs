using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Authentication;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

internal sealed class AuthenticationTestHost : IAsyncDisposable
{
    private bool _apiStopped;
    internal const string AdministratorEmail = "administrator@nexora.test";
    internal const string AdministratorPassword = "Test-only-Strong-password-8291!";
    internal const string DefaultDeviceName = "Private test device 8291";

    private AuthenticationTestHost(PostgresTestDatabase database, ApiFactory factory, HttpClient client, Guid administratorId)
    {
        Database = database;
        Factory = factory;
        Client = client;
        AdministratorId = administratorId;
    }

    public PostgresTestDatabase Database { get; }
    public ApiFactory Factory { get; }
    public HttpClient Client { get; }
    public Guid AdministratorId { get; }

    public static async Task<AuthenticationTestHost> CreateAsync(TimeProvider? clock = null, DataProtectionTestFiles? protectionFiles = null)
    {
        var database = await PostgresTestDatabase.CreateAsync();
        ApiFactory? factory = null;
        try
        {
            await using (var scope = database.CreateContextScope())
            {
                await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.MigrateAsync();
            }

            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Nexora"] = database.ConnectionString
            };
            if (protectionFiles is not null)
            {
                settings["DataProtection:KeyDirectory"] = protectionFiles.KeyDirectory;
                settings["DataProtection:CertificatePath"] = protectionFiles.CertificatePath;
            }

            factory = new ApiFactory(configuration: settings, clock: clock, usePersistentKeys: protectionFiles is not null);
            var client = factory.CreateClient();
            await using var administrationScope = factory.Services.CreateAsyncScope();
            var administration = administrationScope.ServiceProvider.GetRequiredService<IAccountAdministration>();
            var result = await administration.BootstrapAsync(AdministratorEmail, AdministratorPassword, CancellationToken.None);
            Assert.True(result.Succeeded, "O bootstrap da conta de teste deve concluir.");

            return new AuthenticationTestHost(database, factory, client, result.Value);
        }
        catch
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            await database.DisposeAsync();
            throw;
        }
    }

    public async Task<AuthenticationTokens> LoginAsync(Guid? deviceId = null, string? email = null, string? password = null)
    {
        using var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            login = email ?? AdministratorEmail,
            password = password ?? AdministratorPassword,
            deviceName = DefaultDeviceName,
            platform = "integration-tests",
            deviceId
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await ReadTokensAsync(response);
        Assert.Equal(300, tokens.ExpiresIn);
        return tokens;
    }

    public async Task<AuthenticationTokens> RefreshAsync(string refreshToken)
    {
        using var response = await Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadTokensAsync(response);
    }

    public Task<HttpResponseMessage> GetDevicesAsync(string? accessToken = null) =>
        SendAuthorizedAsync(Client, HttpMethod.Get, "/api/devices", accessToken);

    public static async Task<HttpResponseMessage> SendAuthorizedAsync(HttpClient client, HttpMethod method, string path,
        string? accessToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await client.SendAsync(request);
    }

    public static async Task<AuthenticationTokens> ReadTokensAsync(HttpResponseMessage response)
    {
        var tokens = await response.Content.ReadFromJsonAsync<AuthenticationTokens>();
        Assert.NotNull(tokens);
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.InRange(tokens.ExpiresIn, 1, 300);
        Assert.True(!string.IsNullOrWhiteSpace(tokens.AccessToken), "Access token deve existir.");
        Assert.True(!string.IsNullOrWhiteSpace(tokens.RefreshToken), "Refresh token deve existir.");
        return tokens;
    }

    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, body.RootElement.GetProperty("status").GetInt32());
        if (code is not null)
        {
            Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        }

        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }

    public async Task StopApiAsync()
    {
        if (_apiStopped)
        {
            return;
        }

        Client.Dispose();
        await Factory.DisposeAsync();
        _apiStopped = true;
    }

    public async ValueTask DisposeAsync()
    {
        await StopApiAsync();
        await Database.DisposeAsync();
    }
}

internal sealed class MutableTimeProvider : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks / 10 * 10;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}
