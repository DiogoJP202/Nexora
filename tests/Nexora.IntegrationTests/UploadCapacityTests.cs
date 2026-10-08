using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Application.Uploads;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Uploads;

namespace Nexora.IntegrationTests;

public sealed class UploadCapacityTests
{
    [PostgresFact]
    public async Task ReservationBudgetIsGlobalAcrossOwnersAndRefusedAdmissionCreatesNoSession()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        host.Factory.Services.GetRequiredService<IOptions<UploadOptions>>().Value.MaximumReservedBytes = 64;
        var owner = await host.LoginAsync();
        var second = await host.CreateSecondOwnerAsync();
        await host.CreateUploadAsync(owner.AccessToken, new byte[16], "first-owner.bin");
        await host.CreateUploadAsync(second.Tokens.AccessToken, new byte[16], "second-owner.bin");

        using var refused = await host.SendAsync(HttpMethod.Post, "/api/uploads", owner.AccessToken,
            JsonContent.Create(new CreateUploadRequest("over-global-budget.bin", 16)));
        await AuthenticationTestHost.AssertProblemAsync(refused, HttpStatusCode.InsufficientStorage, "insufficient_storage");

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var uploads = await db.UploadSessions.AsNoTracking().ToListAsync();
        Assert.Equal(2, uploads.Count);
        Assert.Equal(64, uploads.Sum(upload => upload.ReservedBytes));
        Assert.Equal(32, Assert.Single(uploads, upload => upload.OwnerId == host.OwnerId).ReservedBytes);
        Assert.Equal(32, Assert.Single(uploads, upload => upload.OwnerId == second.Id).ReservedBytes);
        Assert.Empty(host.Files.Files);
    }

    [PostgresFact]
    public async Task CancelledUploadsKeepReservationsUntilPhysicalCleanupIsCommitted()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        host.Factory.Services.GetRequiredService<IOptions<UploadOptions>>().Value.MaximumReservedBytes = 48;
        var owner = await host.LoginAsync();
        var bytes = new byte[16];
        var upload = await host.CreateUploadAsync(owner.AccessToken, bytes);
        using (var chunk = await host.PutAsync(owner.AccessToken, upload.Id, 0, bytes))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        using (var cancellation = await host.SendAsync(HttpMethod.Delete, $"/api/uploads/{upload.Id}", owner.AccessToken))
            Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);

        var before = await ReadStorageAsync(host, owner.AccessToken);
        Assert.Equal(32, before.ReservedBytes);
        Assert.Equal(16, before.TemporaryBytes);
        await AssertAdmissionRefusedAsync(host, owner.AccessToken);

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IUploadWorkStore>();
            var cleanup = Assert.Single(await store.ListCleanupAsync(10, CancellationToken.None));
            Assert.Equal(upload.Id, cleanup.UploadId);
            var temporary = scope.ServiceProvider.GetRequiredService<ITrackedTemporaryStorage>();
            foreach (var key in cleanup.Keys)
                await temporary.DeleteAttemptAsync(key, preserveCompleted: false, CancellationToken.None);
            Assert.Empty(host.Files.Files);
            // Deleting bytes alone must not make an unreleased reservation available.
            await AssertAdmissionRefusedAsync(host, owner.AccessToken);
            Assert.Equal(32, (await ReadStorageAsync(host, owner.AccessToken)).ReservedBytes);
            Assert.True(await store.FinishCleanupAsync(cleanup.UploadId, CancellationToken.None));
        }

        var after = await ReadStorageAsync(host, owner.AccessToken);
        Assert.Equal(0, after.ReservedBytes);
        Assert.Equal(0, after.TemporaryBytes);
        await host.CreateUploadAsync(owner.AccessToken, bytes, "after-cleanup.bin");
        await using var verification = host.Factory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cancelled = await db.UploadSessions.AsNoTracking().SingleAsync(row => row.Id == upload.Id);
        Assert.Equal(UploadState.Cancelled, cancelled.State);
        Assert.Equal(0, cancelled.ReservedBytes);
        Assert.Equal(32, await db.UploadSessions.SumAsync(row => row.ReservedBytes));
    }

    [PostgresFact]
    public async Task ConcurrentAdmissionsNeverExceedTheGlobalReservationBudget()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        host.Factory.Services.GetRequiredService<IOptions<UploadOptions>>().Value.MaximumReservedBytes = 64;
        var owner = await host.LoginAsync();
        var second = await host.CreateSecondOwnerAsync();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new[] { owner.AccessToken, second.Tokens.AccessToken, owner.AccessToken, second.Tokens.AccessToken };
        var admissions = tokens.Select(async (token, index) =>
        {
            await start.Task;
            using var response = await host.SendAsync(HttpMethod.Post, "/api/uploads", token,
                JsonContent.Create(new CreateUploadRequest($"concurrent-{index}.bin", 16)));
            if (response.StatusCode != HttpStatusCode.Created)
                await AuthenticationTestHost.AssertProblemAsync(response, HttpStatusCode.InsufficientStorage, "insufficient_storage");
            return response.StatusCode;
        }).ToArray();
        start.SetResult(true);
        var results = await Task.WhenAll(admissions);
        Assert.Equal(2, results.Count(status => status == HttpStatusCode.Created));
        Assert.Equal(2, results.Count(status => status == HttpStatusCode.InsufficientStorage));

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var uploads = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>()
            .UploadSessions.AsNoTracking().ToListAsync();
        Assert.Equal(2, uploads.Count);
        Assert.Equal(64, uploads.Sum(upload => upload.ReservedBytes));
        Assert.All(uploads, upload => Assert.Equal(UploadState.Open, upload.State));
        Assert.All(uploads.GroupBy(upload => upload.OwnerId), group => Assert.InRange(group.Count(), 1, 2));
        Assert.Empty(host.Files.Files);
    }

    [PostgresFact]
    public async Task FreeSpaceMarginIncludesBothNewAndExistingReservations()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var owner = await host.LoginAsync();
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<IOptions<UploadOptions>>();
        const long minimumFree = 50L * 1024 * 1024 * 1024;
        options.Value.MinimumFreeBytes = minimumFree;
        var usage = new FixedUsageReader(new StorageUsage(minimumFree * 2, minimumFree + 31, 0, 0, 0));
        var db = services.GetRequiredService<NexoraDbContext>();
        var uploads = new PostgresUploadService(db, services.GetRequiredService<ITemporaryStorage>(), usage, options,
            services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<PostgresUploadService>>());
        var request = new CreateUploadRequest("free-space.bin", 16);

        var firstFailure = await Assert.ThrowsAsync<UploadOperationException>(() =>
            uploads.CreateAsync(host.OwnerId, owner.DeviceId, request, CancellationToken.None));
        Assert.Equal(507, firstFailure.StatusCode);
        Assert.Equal("insufficient_storage", firstFailure.Code);
        Assert.Equal(0, await db.UploadSessions.CountAsync());

        usage.Value = usage.Value with { AvailableBytes = minimumFree + 32 };
        await uploads.CreateAsync(host.OwnerId, owner.DeviceId, request, CancellationToken.None);
        usage.Value = usage.Value with { AvailableBytes = minimumFree + 63 };
        var secondFailure = await Assert.ThrowsAsync<UploadOperationException>(() =>
            uploads.CreateAsync(host.OwnerId, owner.DeviceId, request, CancellationToken.None));
        Assert.Equal(507, secondFailure.StatusCode);
        Assert.Equal("insufficient_storage", secondFailure.Code);
        Assert.Equal(1, await db.UploadSessions.CountAsync());
        Assert.Equal(32, await db.UploadSessions.SumAsync(row => row.ReservedBytes));
        Assert.Empty(host.Files.Files);
    }

    private static async Task AssertAdmissionRefusedAsync(FileApiTestHost host, string accessToken)
    {
        using var refused = await host.SendAsync(HttpMethod.Post, "/api/uploads", accessToken,
            JsonContent.Create(new CreateUploadRequest("awaiting-cleanup.bin", 16)));
        await AuthenticationTestHost.AssertProblemAsync(refused, HttpStatusCode.InsufficientStorage, "insufficient_storage");
    }

    private static async Task<StorageStatusSnapshot> ReadStorageAsync(FileApiTestHost host, string accessToken)
    {
        using var response = await host.SendAsync(HttpMethod.Get, "/api/storage", accessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var storage = await response.Content.ReadFromJsonAsync<StorageStatusSnapshot>();
        Assert.NotNull(storage);
        return storage;
    }

    private sealed class FixedUsageReader(StorageUsage initialValue) : IStorageUsageReader
    {
        public StorageUsage Value { get; set; } = initialValue;
        public Task<StorageUsage> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }
    }
}
