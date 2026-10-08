using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Authentication;
using Nexora.Application.Content;
using Nexora.Application.Jobs;
using Nexora.Application.Uploads;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

internal sealed class FileApiTestHost : IAsyncDisposable
{
    private readonly Dictionary<string, string?> _settings;
    private readonly TimeProvider? _clock;

    private FileApiTestHost(PostgresTestDatabase database, StorageTestFiles files,
        Dictionary<string, string?> settings, ApiFactory factory, HttpClient client, Guid ownerId, TimeProvider? clock)
    {
        Database = database;
        Files = files;
        _settings = settings;
        _clock = clock;
        Factory = factory;
        Client = client;
        OwnerId = ownerId;
    }

    public const int ChunkSize = 16;
    public PostgresTestDatabase Database { get; }
    public StorageTestFiles Files { get; }
    public ApiFactory Factory { get; private set; }
    public HttpClient Client { get; private set; }
    public Guid OwnerId { get; }

    public static async Task<FileApiTestHost> CreateAsync(TimeProvider? clock = null)
    {
        var files = new StorageTestFiles();
        PostgresTestDatabase? database = null;
        ApiFactory? factory = null;
        try
        {
            database = await PostgresTestDatabase.CreateAsync();
            await using (var scope = database.CreateContextScope())
                await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.MigrateAsync();
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Nexora"] = database.ConnectionString,
                ["Storage:RootPath"] = files.RootPath,
                ["Uploads:ChunkSizeBytes"] = ChunkSize.ToString(),
                ["Uploads:MaximumFileSizeBytes"] = "1024",
                ["Uploads:MaximumReservedBytes"] = "2097152",
                ["Uploads:MinimumFreeBytes"] = "0"
            };
            factory = new ApiFactory(configuration: settings, clock: clock);
            var client = factory.CreateClient();
            await using var administrationScope = factory.Services.CreateAsyncScope();
            var administration = administrationScope.ServiceProvider.GetRequiredService<IAccountAdministration>();
            var account = await administration.BootstrapAsync(AuthenticationTestHost.AdministratorEmail,
                AuthenticationTestHost.AdministratorPassword, CancellationToken.None);
            Assert.True(account.Succeeded, "O bootstrap da conta de teste deve concluir.");
            return new FileApiTestHost(database, files, settings, factory, client, account.Value, clock);
        }
        catch
        {
            if (factory is not null) await factory.DisposeAsync();
            if (database is not null) await database.DisposeAsync();
            files.Dispose();
            throw;
        }
    }

    public async Task<AuthenticationTokens> LoginAsync(string? email = null)
    {
        using var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            login = email ?? AuthenticationTestHost.AdministratorEmail,
            password = AuthenticationTestHost.AdministratorPassword,
            deviceName = AuthenticationTestHost.DefaultDeviceName,
            platform = "files-tests"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await AuthenticationTestHost.ReadTokensAsync(response);
    }

    public async Task<(Guid Id, AuthenticationTokens Tokens)> CreateSecondOwnerAsync()
    {
        const string email = "second-owner@nexora.test";
        await using var scope = Factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<NexoraUser>>();
        var owner = new NexoraUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        var result = await manager.CreateAsync(owner, AuthenticationTestHost.AdministratorPassword);
        Assert.True(result.Succeeded, "A segunda conta de teste deve ser criada.");
        return (owner.Id, await LoginAsync(email));
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token,
        HttpContent? body = null, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        configure?.Invoke(request);
        return await Client.SendAsync(request);
    }

    public async Task<UploadSnapshot> CreateUploadAsync(string token, byte[] bytes, string name = "private-example.bin")
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/uploads", token,
            JsonContent.Create(new CreateUploadRequest(name, bytes.Length)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var upload = await response.Content.ReadFromJsonAsync<UploadSnapshot>();
        Assert.NotNull(upload);
        Assert.Equal($"/api/uploads/{upload.Id}", response.Headers.Location?.OriginalString);
        return upload;
    }

    public async Task<HttpResponseMessage> PutAsync(string token, Guid uploadId, int number, byte[] data,
        string? expectedHash = null, string contentType = "application/octet-stream")
    {
        var body = new ByteArrayContent(data);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await SendAsync(HttpMethod.Put, $"/api/uploads/{uploadId}/chunks/{number}", token, body,
            request => { if (expectedHash is not null) request.Headers.Add("X-Chunk-SHA256", expectedHash); });
    }

    public async Task SendChunksAsync(string token, UploadSnapshot upload, byte[] data)
    {
        for (var number = 0; number < upload.ChunkCount; number++)
        {
            var offset = number * upload.ChunkSize;
            using var response = await PutAsync(token, upload.Id, number,
                data.AsSpan(offset, Math.Min(upload.ChunkSize, data.Length - offset)).ToArray());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    public async Task<UploadSnapshot> GetUploadAsync(string token, Guid uploadId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/uploads/{uploadId}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<UploadSnapshot>();
        Assert.NotNull(snapshot);
        return snapshot;
    }

    public async Task ProcessOneAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUploadWorkStore>();
        var work = await store.ClaimAsync(CancellationToken.None);
        Assert.NotNull(work);
        await scope.ServiceProvider.GetRequiredService<IUploadJobProcessor>().ProcessAsync(work, CancellationToken.None);
    }

    public async Task<AssetSnapshot> ImportAsync(Guid ownerId, byte[] data, string name)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await using var content = new MemoryStream(data, writable: false);
        var result = await scope.ServiceProvider.GetRequiredService<IAssetIngestionService>()
            .ImportAsync(new AssetImportRequest(ownerId, name, data.Length), content, CancellationToken.None);
        Assert.NotNull(result.Asset);
        return result.Asset;
    }

    public async Task RestartAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        Factory = new ApiFactory(configuration: _settings, clock: _clock);
        Client = Factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
        Files.Dispose();
    }
}
