using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Application.Images;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Persistence;
using Nexora.Mobile.Core;

namespace Nexora.IntegrationTests;

public sealed class MobileClientFlowTests
{
    [PostgresFact]
    public async Task Mobile_client_recovers_committed_upload_responses_and_synchronizes_library_changes_over_the_real_API()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        using var privateFiles = new StorageTestFiles();
        host.Factory.Services.GetRequiredService<IOptions<UploadOptions>>().Value.MaximumReservedBytes = 64 * 1024 * 1024;
        host.Factory.Services.GetRequiredService<IOptions<ImageOptions>>().Value.MaximumDerivativeBytes = 1024 * 1024;
        using var transport = new InterruptedResponseHandler(host.Factory.Server.CreateHandler());
        using var http = new HttpClient(transport);
        var sessions = new ScopedMemorySessionStore();
        var installation = new ScopedMemoryInstallation();
        var server = ServerScope.Create("http://localhost", AuthenticationTestHost.AdministratorEmail,
            allowLocalDevelopmentHttp: true);
        var client = new NexoraClient(http, sessions, installation);
        await client.ConfigureAsync(server);
        await client.LoginAsync(AuthenticationTestHost.AdministratorPassword, "Mobile wire test", "Android");
        Assert.True(client.IsSignedIn);
        var authenticated = Assert.IsType<SecureSession>(await sessions.GetAsync(server));
        Assert.Equal(authenticated.DeviceId, await installation.GetDeviceIdAsync(server));

        var bytes = ImageTestHost.Encode(24, 12);
        var outbox = new UploadOutbox(privateFiles.RootPath, client);
        using var source = new MemoryStream(bytes, writable: false);
        var queued = await outbox.EnqueueAsync("mobile-upload.png", source);
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(queued.Id));
        var interruptedCreate = Assert.Single(await outbox.ListAsync());
        Assert.Equal(OutboxState.CreateInFlight, interruptedCreate.State);
        Assert.Null(interruptedCreate.ServerUploadId);
        Guid uploadId;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var persisted = Assert.Single(await scope.ServiceProvider.GetRequiredService<NexoraDbContext>()
                .UploadSessions.AsNoTracking().ToArrayAsync());
            uploadId = persisted.Id;
            Assert.Equal(queued.Id, persisted.ClientRequestId);
        }

        // Recreate the app objects from disk while keeping the platform secure store.
        client = new NexoraClient(http, sessions, installation);
        await client.ConfigureAsync(server);
        outbox = new UploadOutbox(privateFiles.RootPath, client);
        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ResumeAsync(queued.Id));
        var interruptedChunk = Assert.Single(await outbox.ListAsync());
        Assert.Equal(uploadId, interruptedChunk.ServerUploadId);
        Assert.Empty(interruptedChunk.ConfirmedChunks);
        var committedChunk = await client.GetUploadAsync(uploadId);
        Assert.Equal(UploadState.Open, committedChunk.State);
        Assert.Equal(new[] { 0 }, committedChunk.ConfirmedChunks);

        client = new NexoraClient(http, sessions, installation);
        await client.ConfigureAsync(server);
        outbox = new UploadOutbox(privateFiles.RootPath, client);
        var finalizing = await outbox.ResumeAsync(queued.Id);
        Assert.Equal(OutboxState.Finalizing, finalizing.State);
        Assert.Equal(uploadId, finalizing.ServerUploadId);
        Assert.Equal(Enumerable.Range(0, committedChunk.ChunkCount), transport.Chunks);
        Assert.Equal(2, transport.CreateRequests);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().UploadSessions.CountAsync());

        await host.ProcessOneAsync();
        var completed = await outbox.ResumeAsync(queued.Id);
        Assert.Equal(OutboxState.Completed, completed.State);
        var result = Assert.IsType<AssetSnapshot>(completed.Result);
        Assert.Equal("mobile-upload.png", result.OriginalName);
        Assert.Equal(bytes.Length, result.Size);
        Assert.Equal("image/png", result.DetectedMimeType);
        Assert.Equal(ImageProcessingState.Pending, result.Image?.State);
        Assert.False(File.Exists(Path.Combine(privateFiles.RootPath, queued.SourcePath)));
        var uploaded = await client.GetUploadAsync(uploadId);
        Assert.Equal(UploadState.Completed, uploaded.State);
        Assert.Equal(BackgroundJobState.Succeeded, uploaded.Operation?.State);

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var imageWork = await services.GetRequiredService<IImageWorkStore>().ClaimAsync(CancellationToken.None);
            Assert.NotNull(imageWork);
            await services.GetRequiredService<IImageJobProcessor>().ProcessAsync(imageWork, CancellationToken.None);
        }
        var cache = new LocalLibraryCache(privateFiles.RootPath, client);
        var initial = await cache.SynchronizeAsync();
        var cached = Assert.Single(initial.Items);
        Assert.Equal(result.Id, cached.Id);
        Assert.Equal(ImageProcessingState.Ready, cached.Image?.State);
        Assert.Equal(24, cached.Image?.Width);
        Assert.Equal(12, cached.Image?.Height);
        Assert.True(cached.Image?.HasThumbnail);
        Assert.True(cached.Image?.HasPreview);
        Assert.NotNull(initial.Cursor);
        using (var original = new MemoryStream())
        {
            await client.DownloadAsync(result.Id, DownloadKind.Original, original);
            Assert.Equal(bytes, original.ToArray());
        }
        using (var preview = new MemoryStream())
        {
            await client.DownloadAsync(result.Id, DownloadKind.Preview, preview);
            ImageTestHost.AssertDimensions(preview.ToArray(), 24, 12);
        }

        Assert.True((await client.SetFavoriteAsync(result.Id, true)).IsFavorite);
        Assert.True(Assert.Single((await cache.SynchronizeAsync()).Items).IsFavorite);
        await client.MoveToTrashAsync(result.Id);
        Assert.NotNull(Assert.Single((await cache.SynchronizeAsync()).Items).DeletedAt);
        Assert.Empty((await client.ListAssetsAsync()).Items);
        Assert.Equal(result.Id, Assert.Single((await client.ListAssetsAsync(new LibraryQuery(Trash: true))).Items).Id);
        Assert.Null((await client.RestoreAsync(result.Id)).DeletedAt);
        var restored = Assert.Single((await cache.SynchronizeAsync()).Items);
        Assert.Null(restored.DeletedAt);
        Assert.True(restored.IsFavorite);
        Assert.Equal(result.Id, Assert.Single((await client.ListAssetsAsync(new LibraryQuery(IsFavorite: true, ImagesOnly: true))).Items).Id);
        Assert.Equal(1, transport.SnapshotRequests);
        Assert.Equal(3, transport.ChangesRequests);
        Assert.Equal(restored, Assert.Single((await new LocalLibraryCache(privateFiles.RootPath, client).GetCachedAsync()).Items));

        await client.LogoutAsync();
        Assert.False(client.IsSignedIn);
        Assert.Null(await sessions.GetAsync(server));
        await Assert.ThrowsAsync<LoginRequiredException>(() => client.ListAssetsAsync());
        using var revoked = await host.SendAsync(HttpMethod.Get, "/api/assets", authenticated.AccessToken);
        await AuthenticationTestHost.AssertProblemAsync(revoked, HttpStatusCode.Unauthorized, "authentication_required");
        var metadata = await File.ReadAllTextAsync(Path.Combine(privateFiles.RootPath, "library", server.Key, "metadata.json"));
        Assert.DoesNotContain(authenticated.AccessToken, metadata);
        Assert.DoesNotContain(authenticated.RefreshToken, metadata);
    }

    // Forward every request to TestServer, then discard two already committed responses.
    private sealed class InterruptedResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool loseCreate = true;
        private bool loseChunk = true;
        internal int CreateRequests { get; private set; }
        internal int SnapshotRequests { get; private set; }
        internal int ChangesRequests { get; private set; }
        internal List<int> Chunks { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var create = request.Method == HttpMethod.Post && path == "/api/uploads";
            var chunk = request.Method == HttpMethod.Put && path.Contains("/chunks/", StringComparison.Ordinal);
            if (create) CreateRequests++;
            if (chunk) Chunks.Add(int.Parse(path.Split('/')[^1], CultureInfo.InvariantCulture));
            if (path == "/api/sync") SnapshotRequests++;
            if (path == "/api/sync/changes") ChangesRequests++;
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode && ((create && loseCreate) || (chunk && loseChunk)))
            {
                if (create) loseCreate = false; else loseChunk = false;
                response.Dispose();
                throw new HttpRequestException("Simulated lost acknowledgement after the API committed.");
            }
            return response;
        }
    }

    private sealed class ScopedMemorySessionStore : ISecureSessionStore
    {
        private readonly Dictionary<string, SecureSession> values = [];
        public Task<SecureSession?> GetAsync(ServerScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.GetValueOrDefault(scope.Key));
        public Task SaveAsync(ServerScope scope, SecureSession session, CancellationToken cancellationToken = default)
        { values[scope.Key] = session; return Task.CompletedTask; }
        public Task ClearAsync(ServerScope scope, CancellationToken cancellationToken = default)
        { values.Remove(scope.Key); return Task.CompletedTask; }
    }

    private sealed class ScopedMemoryInstallation : IInstallationIdentity
    {
        private readonly Dictionary<string, Guid> values = [];
        public Task<Guid?> GetDeviceIdAsync(ServerScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(values.TryGetValue(scope.Key, out var device) ? device : null);
        public Task SetDeviceIdAsync(ServerScope scope, Guid deviceId, CancellationToken cancellationToken = default)
        { values[scope.Key] = deviceId; return Task.CompletedTask; }
    }
}
