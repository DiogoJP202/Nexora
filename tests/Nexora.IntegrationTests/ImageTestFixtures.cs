using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Content;
using Nexora.Application.Images;
using Nexora.Application.Storage;
using Nexora.Infrastructure.Persistence;
using SkiaSharp;

namespace Nexora.IntegrationTests;

internal sealed class ImageTestHost : IAsyncDisposable
{
    private readonly FileApiTestHost _files;
    private ImageTestHost(FileApiTestHost files, ApiFactory factory)
    {
        _files = files;
        Factory = factory;
        Client = factory.CreateClient();
    }

    public ApiFactory Factory { get; }
    public HttpClient Client { get; }
    public Guid OwnerId => _files.OwnerId;
    public StorageTestFiles Files => _files.Files;

    public static async Task<ImageTestHost> CreateAsync(TimeProvider? clock = null,
        IReadOnlyDictionary<string, string?>? additionalSettings = null)
    {
        var files = await FileApiTestHost.CreateAsync(clock);
        ApiFactory? factory = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Nexora"] = files.Database.ConnectionString,
                ["Storage:RootPath"] = files.Files.RootPath,
                ["Uploads:MaximumReservedBytes"] = "67108864",
                ["Uploads:MinimumFreeBytes"] = "0",
                ["Images:MaximumDerivativeBytes"] = "1048576"
            };
            if (additionalSettings is not null)
                foreach (var entry in additionalSettings) settings[entry.Key] = entry.Value;
            factory = new ApiFactory(configuration: settings, clock: clock);
            return new ImageTestHost(files, factory);
        }
        catch
        {
            if (factory is not null) await factory.DisposeAsync();
            await files.DisposeAsync();
            throw;
        }
    }

    public async Task<string> LoginAsync(string? email = null)
    {
        using var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            login = email ?? AuthenticationTestHost.AdministratorEmail,
            password = AuthenticationTestHost.AdministratorPassword,
            deviceName = "Private image test installation",
            platform = "images-tests"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await AuthenticationTestHost.ReadTokensAsync(response)).AccessToken;
    }

    public async Task<(Guid Id, string Token)> CreateSecondOwnerAsync()
    {
        var owner = await _files.CreateSecondOwnerAsync();
        return (owner.Id, await LoginAsync("second-owner@nexora.test"));
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token,
        Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        configure?.Invoke(request);
        return SendAndDisposeAsync(request);
    }

    private async Task<HttpResponseMessage> SendAndDisposeAsync(HttpRequestMessage request)
    {
        using (request) return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> SendContentAsync(HttpMethod method, string path, string token, HttpContent content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return SendAndDisposeAsync(request);
    }

    public async Task<AssetSnapshot> ImportAsync(byte[] bytes, string name = "private-image.png", Guid? ownerId = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await using var stream = new MemoryStream(bytes, writable: false);
        var result = await scope.ServiceProvider.GetRequiredService<IAssetIngestionService>()
            .ImportAsync(new AssetImportRequest(ownerId ?? OwnerId, name, bytes.Length), stream, CancellationToken.None);
        Assert.NotNull(result.Asset);
        return result.Asset;
    }

    public async Task<ImageWorkItem> ClaimAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var work = await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().ClaimAsync(CancellationToken.None);
        Assert.NotNull(work);
        return work;
    }

    public async Task ProcessAsync(ImageWorkItem work)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IImageJobProcessor>().ProcessAsync(work, CancellationToken.None);
    }

    public async Task CleanupAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IImageWorkStore>();
        var storage = scope.ServiceProvider.GetRequiredService<IDerivativeStorage>();
        foreach (var item in await store.ListCleanupAsync(100, CancellationToken.None))
        {
            foreach (var attempt in item.Attempts)
                await storage.CleanAttemptAsync(attempt, item.PublishedGeneration == attempt, CancellationToken.None);
            Assert.True(await store.FinishCleanupAsync(item.JobId, CancellationToken.None));
        }
    }

    public async Task<AssetSnapshot> GetAssetAsync(Guid id, string token)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/assets/{id}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var asset = await response.Content.ReadFromJsonAsync<AssetSnapshot>();
        Assert.NotNull(asset);
        return asset;
    }

    public async Task<long> ReservedBytesAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Set<Nexora.Domain.Images.BlobImage>()
            .SumAsync(image => image.ReservedBytes);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _files.DisposeAsync();
    }

    public static byte[] Encode(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(40, 100, 170));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        Assert.NotNull(data);
        return data.ToArray();
    }

    public static void AssertDimensions(byte[] png, int width, int height)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
    }
}
