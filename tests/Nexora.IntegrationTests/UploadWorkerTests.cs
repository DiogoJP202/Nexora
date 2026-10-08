using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Application.Content;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Application.Uploads;
using Nexora.Domain.Content;
using Nexora.Domain.Jobs;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class UploadWorkerTests
{
    [PostgresFact]
    public async Task Restart_reclaims_lease_and_removes_partial_before_reassembling()
    {
        var clock = new MutableTimeProvider();
        await using var host = await FileApiTestHost.CreateAsync(clock);
        var upload = await QueueAsync(host);
        var abandoned = await ClaimAsync(host);
        var partial = Path.Combine(host.Files.RootPath, "temp", abandoned.Lease.Token.ToString("N") + ".part");
        await File.WriteAllBytesAsync(partial, Data);
        Assert.Equal(Data.Length * 2, TemporaryBytes(host));
        clock.Advance(TimeSpan.FromMinutes(2));
        await host.RestartAsync();
        var resumed = await ClaimAsync(host);
        Assert.Equal(abandoned.Lease.JobId, resumed.Lease.JobId);
        Assert.NotEqual(abandoned.Lease.Token, resumed.Lease.Token);
        Assert.Contains(new TemporaryObjectKey(abandoned.Lease.Token), resumed.PreviousAttempts);
        await ProcessAsync(host, resumed);
        Assert.False(File.Exists(partial));
        Assert.Equal(Data.Length, TemporaryBytes(host));
        await AssertCompletedAsync(host, upload.Id);
        await CleanupAsync(host);
        Assert.Equal(0, TemporaryBytes(host));
        await AssertNoReservationAsync(host, upload.Id);
    }

    [PostgresFact]
    public async Task Ambiguous_assembly_commit_preserves_referenced_bytes_for_retry()
    {
        var clock = new MutableTimeProvider();
        await using var host = await FileApiTestHost.CreateAsync(clock);
        var upload = await QueueAsync(host);
        var work = await ClaimAsync(host);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IUploadWorkStore>();
            var processor = new UploadJobProcessor(scope.ServiceProvider.GetRequiredService<ITrackedTemporaryStorage>(),
                new AmbiguousAssemblyCommitStore(store), scope.ServiceProvider.GetRequiredService<IStagedAssetIngestionService>());
            await Assert.ThrowsAsync<IOException>(() => processor.ProcessAsync(work, CancellationToken.None));
            var recorded = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().UploadSessions
                .AsNoTracking().SingleAsync(item => item.Id == upload.Id);
            Assert.Equal(work.Lease.Token, recorded.AssemblyTemporaryId);
            Assert.True(File.Exists(Path.Combine(host.Files.RootPath, "temp", work.Lease.Token.ToString("N") + ".chunk")));
            Assert.True(await store.FailAsync(work.Lease, "storage_unavailable", true, CancellationToken.None));
        }
        clock.Advance(TimeSpan.FromSeconds(31));
        await host.RestartAsync();
        var retry = await ClaimAsync(host);
        Assert.NotNull(retry.Assembly);
        Assert.Equal(work.Lease.Token, retry.Assembly.Key.Id);
        await ProcessAsync(host, retry);
        await AssertCompletedAsync(host, upload.Id);
        await CleanupAsync(host);
        await AssertNoReservationAsync(host, upload.Id);
    }

    [PostgresFact]
    public async Task Publication_failure_is_retried_from_recorded_assembly_and_same_blob_generation()
    {
        var clock = new MutableTimeProvider();
        await using var host = await FileApiTestHost.CreateAsync(clock);
        var upload = await QueueAsync(host);
        var first = await ClaimAsync(host);
        Guid blobId;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var ingestion = new StagedAssetIngestionService(services.GetRequiredService<ITemporaryStorage>(),
                new PublicationFailureStorage(services.GetRequiredService<ITrackedBlobStorage>()),
                services.GetRequiredService<IContentCatalog>(), services.GetRequiredService<IUploadContentCatalog>(), clock);
            var processor = new UploadJobProcessor(services.GetRequiredService<ITrackedTemporaryStorage>(),
                services.GetRequiredService<IUploadWorkStore>(), ingestion);
            await Assert.ThrowsAsync<IOException>(() => processor.ProcessAsync(first, CancellationToken.None));
            var db = services.GetRequiredService<NexoraDbContext>();
            var blob = await db.Blobs.AsNoTracking().SingleAsync();
            blobId = blob.Id;
            Assert.Equal(BlobState.Staging, blob.State);
            Assert.Empty(await db.Assets.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.UploadChunks.AsNoTracking().ToArrayAsync());
            Assert.True(await services.GetRequiredService<IUploadWorkStore>()
                .FailAsync(first.Lease, "storage_unavailable", true, CancellationToken.None));
        }
        clock.Advance(TimeSpan.FromSeconds(31));
        await host.RestartAsync();
        await ProcessAsync(host, await ClaimAsync(host));
        await AssertCompletedAsync(host, upload.Id);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(blobId, (await db.Blobs.AsNoTracking().SingleAsync()).Id);
            Assert.Single(await db.Assets.AsNoTracking().ToArrayAsync());
            Assert.Equal(BackgroundJobState.Succeeded, (await db.BackgroundJobs.AsNoTracking().SingleAsync()).State);
        }
        await CleanupAsync(host);
        await AssertNoReservationAsync(host, upload.Id);
    }

    [PostgresFact]
    public async Task Cancelled_claim_cannot_publish_asset_and_cleanup_waits_old_lease()
    {
        var clock = new MutableTimeProvider();
        await using var host = await FileApiTestHost.CreateAsync(clock);
        var upload = await QueueAsync(host);
        var work = await ClaimAsync(host);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IUploadService>()
                .CancelAsync(host.OwnerId, upload.Id, CancellationToken.None));
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<IUploadWorkStore>()
                .ListCleanupAsync(10, CancellationToken.None));
        }
        await Assert.ThrowsAsync<JobLeaseLostException>(() => ProcessAsync(host, work));
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Assets.ToArrayAsync());
        clock.Advance(TimeSpan.FromMinutes(2));
        await CleanupAsync(host);
        await AssertNoReservationAsync(host, upload.Id);
        Assert.Equal(0, TemporaryBytes(host));
    }

    [PostgresFact]
    public async Task Concurrent_claims_produce_one_lease_and_exhausted_jobs_remain_visible()
    {
        var clock = new MutableTimeProvider();
        await using var host = await FileApiTestHost.CreateAsync(clock);
        var upload = await QueueAsync(host);
        async Task<UploadWorkItem?> TryClaimAsync()
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IUploadWorkStore>().ClaimAsync(CancellationToken.None);
        }
        var results = await Task.WhenAll(TryClaimAsync(), TryClaimAsync());
        Assert.Single(results, result => result is not null);
        for (var attempt = 1; attempt < 5; attempt++)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.NotNull(await TryClaimAsync());
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await TryClaimAsync());
        await using var inspection = host.Factory.Services.CreateAsyncScope();
        var db = inspection.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(5, job.Attempts);
        Assert.Equal(BackgroundJobState.Failed, job.State);
        Assert.NotNull(job.FailureCode);
        Assert.Equal(UploadState.Failed, (await db.UploadSessions.AsNoTracking().SingleAsync(item => item.Id == upload.Id)).State);
        await CleanupAsync(host);
        await AssertNoReservationAsync(host, upload.Id);
    }

    [PostgresFact]
    public async Task Expiration_retains_reservation_until_chunks_have_been_deleted()
    {
        var clock = new MutableTimeProvider();
        await using var host = await FileApiTestHost.CreateAsync(clock);
        var tokens = await host.LoginAsync();
        var upload = await host.CreateUploadAsync(tokens.AccessToken, Data);
        await host.SendChunksAsync(tokens.AccessToken, upload, Data);
        clock.Advance(TimeSpan.FromDays(8));
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IUploadWorkStore>().ExpireOpenAsync(CancellationToken.None));
            var row = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().UploadSessions.AsNoTracking().SingleAsync();
            Assert.Equal(UploadState.Expired, row.State);
            Assert.Equal(Data.Length * 2, row.ReservedBytes);
        }
        await CleanupAsync(host);
        await AssertNoReservationAsync(host, upload.Id);
        Assert.Equal(0, TemporaryBytes(host));
    }

    [PostgresFact]
    public async Task Wrong_final_hash_is_a_visible_permanent_failure_and_original_is_not_created()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        UploadSnapshot upload;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            upload = await scope.ServiceProvider.GetRequiredService<IUploadService>().CreateAsync(host.OwnerId,
                tokens.DeviceId, new CreateUploadRequest("expected.bin", Data.Length, new string('a', 64)), CancellationToken.None);
        await host.SendChunksAsync(tokens.AccessToken, upload, Data);
        using (var response = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", tokens.AccessToken))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var work = await ClaimAsync(host);
        await Assert.ThrowsAsync<StorageIntegrityException>(() => ProcessAsync(host, work));
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IUploadWorkStore>()
                .FailAsync(work.Lease, "content_integrity", false, CancellationToken.None));
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Blobs.ToArrayAsync());
        }
        var snapshot = await host.GetUploadAsync(tokens.AccessToken, upload.Id);
        Assert.Equal(UploadState.Failed, snapshot.State);
        Assert.Equal("content_integrity", snapshot.FailureCode);
        Assert.Equal(BackgroundJobState.Failed, snapshot.Operation!.State);
        await CleanupAsync(host);
        await AssertNoReservationAsync(host, upload.Id);
    }

    private static byte[] Data => Enumerable.Range(0, 40).Select(number => (byte)number).ToArray();
    private static long TemporaryBytes(FileApiTestHost host) => Directory.EnumerateFiles(Path.Combine(host.Files.RootPath,
        "temp")).Sum(path => new FileInfo(path).Length);

    private static async Task<UploadSnapshot> QueueAsync(FileApiTestHost host)
    {
        var tokens = await host.LoginAsync();
        var upload = await host.CreateUploadAsync(tokens.AccessToken, Data);
        await host.SendChunksAsync(tokens.AccessToken, upload, Data);
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return upload;
    }

    private static async Task<UploadWorkItem> ClaimAsync(FileApiTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var work = await scope.ServiceProvider.GetRequiredService<IUploadWorkStore>().ClaimAsync(CancellationToken.None);
        Assert.NotNull(work);
        return work;
    }

    private static async Task ProcessAsync(FileApiTestHost host, UploadWorkItem work)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUploadJobProcessor>().ProcessAsync(work, CancellationToken.None);
    }

    private static async Task CleanupAsync(FileApiTestHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUploadWorkStore>();
        var storage = scope.ServiceProvider.GetRequiredService<ITrackedTemporaryStorage>();
        foreach (var candidate in await store.ListCleanupAsync(100, CancellationToken.None))
        {
            foreach (var key in candidate.Keys) await storage.DeleteAttemptAsync(key, false, CancellationToken.None);
            Assert.True(await store.FinishCleanupAsync(candidate.UploadId, CancellationToken.None));
        }
    }

    private static async Task AssertCompletedAsync(FileApiTestHost host, Guid uploadId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IUploadService>().GetAsync(host.OwnerId, uploadId, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(UploadState.Completed, snapshot.State);
        Assert.NotNull(snapshot.Result);
        var content = await scope.ServiceProvider.GetRequiredService<IAssetLibrary>().OpenContentAsync(host.OwnerId,
            snapshot.Result.Id, CancellationToken.None);
        Assert.NotNull(content);
        await using var stream = content.Content;
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(Data, output.ToArray());
    }

    private static async Task AssertNoReservationAsync(FileApiTestHost host, Guid uploadId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, (await db.UploadSessions.AsNoTracking().SingleAsync(item => item.Id == uploadId)).ReservedBytes);
        Assert.Empty(await db.UploadChunks.AsNoTracking().Where(chunk => chunk.UploadSessionId == uploadId).ToArrayAsync());
        Assert.Empty(await db.BackgroundJobAttempts.AsNoTracking().ToArrayAsync());
    }

    private sealed class PublicationFailureStorage(ITrackedBlobStorage actual) : ITrackedBlobStorage
    {
        public async Task<BlobPublicationResult> PublishWithAttemptAsync(BlobStorageKey key, TemporaryObjectKey attemptKey,
            Stream content, long expectedLength, string hash, CancellationToken cancellationToken)
        {
            await actual.PublishWithAttemptAsync(key, attemptKey, content, expectedLength, hash, cancellationToken);
            throw new IOException("Simulated interruption after physical publication.");
        }
        public Task<BlobPublicationResult> PublishAsync(BlobStorageKey key, Stream content, long length, string hash, CancellationToken ct)
            => actual.PublishAsync(key, content, length, hash, ct);
        public Task<Stream> OpenReadAsync(BlobStorageKey key, CancellationToken ct) => actual.OpenReadAsync(key, ct);
        public Task<BlobObjectInfo?> GetInfoAsync(BlobStorageKey key, CancellationToken ct) => actual.GetInfoAsync(key, ct);
        public Task DeleteAsync(BlobStorageKey key, CancellationToken ct) => actual.DeleteAsync(key, ct);
    }

    private sealed class AmbiguousAssemblyCommitStore(IUploadWorkStore actual) : IUploadWorkStore
    {
        public async Task<bool> SaveAssemblyAsync(JobLease lease, TemporaryObjectInfo assembly, CancellationToken ct)
        {
            Assert.True(await actual.SaveAssemblyAsync(lease, assembly, ct));
            throw new IOException("Simulated lost acknowledgment after assembly commit.");
        }
        public Task<UploadWorkItem?> ClaimAsync(CancellationToken ct) => actual.ClaimAsync(ct);
        public Task<bool> RenewAsync(JobLease lease, CancellationToken ct) => actual.RenewAsync(lease, ct);
        public Task<bool> RemoveChunkAsync(JobLease lease, int number, CancellationToken ct) => actual.RemoveChunkAsync(lease, number, ct);
        public Task<bool> FailAsync(JobLease lease, string code, bool retryable, CancellationToken ct) => actual.FailAsync(lease, code, retryable, ct);
        public Task<int> ExpireOpenAsync(CancellationToken ct) => actual.ExpireOpenAsync(ct);
        public Task<UploadCleanupItem[]> ListCleanupAsync(int limit, CancellationToken ct) => actual.ListCleanupAsync(limit, ct);
        public Task<bool> FinishCleanupAsync(Guid uploadId, CancellationToken ct) => actual.FinishCleanupAsync(uploadId, ct);
        public Task<HashSet<Guid>> ReferencedTemporaryIdsAsync(CancellationToken ct) => actual.ReferencedTemporaryIdsAsync(ct);
    }
}
