using System.Net;
using System.Net.Http.Json;
using Nexora.Mobile.Core;

namespace Nexora.Mobile.Tests;

public sealed class ClientTests
{
    [Fact]
    public async Task ConcurrentCallsRotateRefreshExactlyOnceAndPersistBeforeNextUse()
    {
        var fixture = new ClientFixture(expired: true);
        var refreshes = 0;
        fixture.Handler.Handle = async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/refresh")
            {
                Assert.Null(fixture.Store.Session);
                Assert.Null(request.Headers.Authorization);
                Interlocked.Increment(ref refreshes);
                await Task.Delay(30, cancellationToken);
                return fixture.Tokens("new-access", "new-refresh");
            }
            Assert.Equal("new-access", request.Headers.Authorization!.Parameter);
            return MockHandler.Json(new AssetPage([], null));
        };
        await fixture.InitializeAsync();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => fixture.Client.ListAssetsAsync()));
        Assert.Equal(1, refreshes);
        Assert.Equal("new-refresh", fixture.Store.Session!.RefreshToken);
        Assert.Equal(1, fixture.Store.Clears);
    }

    [Fact]
    public async Task LostRefreshResponseCannotReplayAfterRetryOrAppRestart()
    {
        var fixture = new ClientFixture(expired: true);
        var calls = 0;
        fixture.Handler.Handle = (_, _) => { calls++; throw new HttpRequestException("Simulated loss"); };
        await fixture.InitializeAsync();
        await Assert.ThrowsAsync<LoginRequiredException>(() => fixture.Client.ListAssetsAsync());
        await Assert.ThrowsAsync<LoginRequiredException>(() => fixture.Client.ListAssetsAsync());
        var restarted = fixture.NewClient();
        await restarted.ConfigureAsync(fixture.Scope);
        await Assert.ThrowsAsync<LoginRequiredException>(() => restarted.ListAssetsAsync());
        Assert.Equal(1, calls);
        Assert.Null(fixture.Store.Session);
        Assert.False(fixture.Client.IsSignedIn);
    }

    [Fact]
    public async Task RevokedDeviceClearsCredentialsAndDoesNotRefreshUnauthorizedRequest()
    {
        var fixture = new ClientFixture();
        var calls = 0;
        fixture.Handler.Handle = (_, _) =>
        {
            calls++;
            return Task.FromResult(MockHandler.Json(new { code = "authentication_required" }, HttpStatusCode.Unauthorized));
        };
        await fixture.InitializeAsync();
        await Assert.ThrowsAsync<LoginRequiredException>(() => fixture.Client.ListAssetsAsync());
        await Assert.ThrowsAsync<LoginRequiredException>(() => fixture.Client.ListAssetsAsync());
        Assert.Equal(1, calls);
        Assert.Null(fixture.Store.Session);
    }

    [Fact]
    public async Task LoginKeepsDeviceIdAcrossServerAndAccountScopedInstallations()
    {
        var fixture = new ClientFixture();
        fixture.Store.Session = null;
        fixture.Identity.Devices[fixture.Scope.Key] = fixture.Device;
        fixture.Handler.Handle = async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadFromJsonAsync<LoginBody>(cancellationToken);
            Assert.Equal(fixture.Device, body!.DeviceId);
            Assert.Equal("alice@example.test", body.Login);
            return fixture.Tokens("opaque-access", "opaque-refresh");
        };
        await fixture.InitializeAsync();
        await fixture.Client.LoginAsync("password", "Test device");
        Assert.True(fixture.Client.IsSignedIn);
        Assert.Equal(fixture.Device, fixture.Identity.Devices[fixture.Scope.Key]);
        Assert.DoesNotContain("opaque-access", fixture.Store.Session!.ToString());
        Assert.DoesNotContain("opaque-refresh", fixture.Store.Session.ToString());
    }

    [Theory]
    [InlineData("http://example.test", false)]
    [InlineData("http://192.168.1.2", true)]
    [InlineData("https://alice:secret@example.test", false)]
    [InlineData("https://example.test/api", false)]
    [InlineData("https://example.test/?token=secret", false)]
    public void UntrustedServerUrlsAreRejected(string url, bool development)
    {
        Assert.Throws<ArgumentException>(() => ServerScope.Create(url, "alice", development));
    }

    [Fact]
    public void EmulatorHttpRequiresExplicitDevelopmentSetting()
    {
        Assert.Throws<ArgumentException>(() => ServerScope.Create("http://10.0.2.2:5080", "alice"));
        Assert.Equal("http://10.0.2.2:5080/", ServerScope.Create("http://10.0.2.2:5080", "alice", true).Server.AbsoluteUri);
        Assert.Equal(ServerScope.Create("https://EXAMPLE.test:443/", "Alice").Key,
            ServerScope.Create("https://example.test", "alice").Key);
    }

    [Fact]
    public async Task RedirectIsNotFollowedAndApiErrorDoesNotExposeUntrustedProblemBody()
    {
        var fixture = new ClientFixture();
        var calls = 0;
        fixture.Handler.Handle = (_, _) =>
        {
            calls++;
            var response = MockHandler.Json(new { detail = "secret-password", code = "bad<script>" }, HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://attacker.test/");
            return Task.FromResult(response);
        };
        await fixture.InitializeAsync();
        var error = await Assert.ThrowsAsync<NexoraApiException>(() => fixture.Client.ListAssetsAsync());
        Assert.Equal("http_302", error.Code);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OriginalIsOnlyDownloadedAsAttachmentAndPreviewOnlyAsPng()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Handle = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not a safe preview</html>")
        });
        await fixture.InitializeAsync();
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.DownloadAsync(Guid.NewGuid(), DownloadKind.Original, output));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.DownloadAsync(Guid.NewGuid(), DownloadKind.Preview, output));
        Assert.Equal(0, output.Length);
    }

    private sealed record LoginBody(string Login, string Password, string DeviceName, string Platform, Guid? DeviceId);
}

internal sealed class MockHandler : HttpMessageHandler
{
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handle { get; set; } =
        (_, _) => throw new InvalidOperationException("Unexpected request");
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await Handle(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }
    internal static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };
}

internal sealed class MemorySecureStore : ISecureSessionStore
{
    internal SecureSession? Session { get; set; }
    internal string? Key { get; set; }
    internal int Clears { get; private set; }
    public Task<SecureSession?> GetAsync(ServerScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(scope.Key == Key ? Session : null);
    public Task SaveAsync(ServerScope scope, SecureSession session, CancellationToken cancellationToken = default)
    { Key = scope.Key; Session = session; return Task.CompletedTask; }
    public Task ClearAsync(ServerScope scope, CancellationToken cancellationToken = default)
    { if (Key == scope.Key) Session = null; Clears++; return Task.CompletedTask; }
}
internal sealed class MemoryIdentity : IInstallationIdentity
{
    internal Dictionary<string, Guid> Devices { get; } = [];
    public Task<Guid?> GetDeviceIdAsync(ServerScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(Devices.TryGetValue(scope.Key, out var id) ? id : null);
    public Task SetDeviceIdAsync(ServerScope scope, Guid deviceId, CancellationToken cancellationToken = default)
    { Devices[scope.Key] = deviceId; return Task.CompletedTask; }
}
internal sealed class TestClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class ClientFixture
{
    internal ServerScope Scope { get; } = ServerScope.Create("https://server.example.test", "alice@example.test");
    internal MemorySecureStore Store { get; } = new();
    internal MemoryIdentity Identity { get; } = new();
    internal TestClock Clock { get; } = new();
    internal MockHandler Handler { get; } = new();
    internal Guid Device { get; } = Guid.NewGuid();
    internal Guid SessionId { get; } = Guid.NewGuid();
    internal NexoraClient Client { get; }
    internal ClientFixture(bool expired = false)
    {
        Store.Key = Scope.Key;
        Store.Session = new SecureSession
        {
            AccessToken = "access-secret", RefreshToken = "refresh-secret", DeviceId = Device, SessionId = SessionId,
            AccessExpiresAt = Clock.Now.AddMinutes(expired ? -1 : 5), SessionExpiresAt = Clock.Now.AddDays(30)
        };
        Client = NewClient();
    }
    internal NexoraClient NewClient() => new(new HttpClient(Handler, disposeHandler: false), Store, Identity, Clock);
    internal Task InitializeAsync() => Client.ConfigureAsync(Scope);
    internal HttpResponseMessage Tokens(string access, string refresh) => MockHandler.Json(new
    {
        tokenType = "Bearer", accessToken = access, expiresIn = 300L, refreshToken = refresh,
        deviceId = Device, sessionId = SessionId, sessionExpiresAt = Clock.Now.AddDays(30)
    });
}

internal sealed class PrivateDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexora-mobile-tests", Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
