using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Application.Content;
using Nexora.Application.Images;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Domain.Content;
using Nexora.Domain.Jobs;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

public sealed class LibraryMaintenanceTests
{
    [PostgresFact]
    public async Task Trash_retention_boundary_and_batch_limit_are_enforced()
    {
        await using var host = await LibraryMaintenanceTestHost.CreateAsync();
        var owner = await host.CreateOwnerAsync();
        var first = await host.ImportAsync(owner, Bytes("first retained original"));
        var second = await host.ImportAsync(owner, Bytes("second retained original"));
        await host.TrashAsync(owner, first.Id);
        await host.TrashAsync(owner, second.Id);

        host.Clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await host.PurgeAsync());
        Assert.Equal(0, await host.CollectAsync());
        Assert.Equal(2, await host.CountAssetsAsync());

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await host.PurgeAsync(1));
        Assert.Equal(1, await host.CountAssetsAsync());
        Assert.Equal(1, await host.PurgeAsync(1));
        Assert.Equal(0, await host.PurgeAsync());
        Assert.Equal(2, await host.CollectAsync());
        Assert.Equal(0, await host.CountBlobsAsync());
    }

    [PostgresFact]
    public async Task Active_and_trashed_references_from_another_owner_preserve_the_shared_blob()
    {
        await using var host = await LibraryMaintenanceTestHost.CreateAsync();
        var owner = await host.CreateOwnerAsync();
        var other = await host.CreateOwnerAsync();
        var bytes = Bytes("shared content with independent owners");
        var first = await host.ImportAsync(owner, bytes);
        var second = await host.ImportAsync(other, bytes);
        var blob = await host.FindBlobAsync(first.Id);
        Assert.Equal(blob.Id, (await host.FindBlobAsync(second.Id)).Id);
        await host.TrashAsync(owner, first.Id);
        host.Clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(1, await host.PurgeAsync());
        Assert.Equal(0, await host.CollectAsync());
        Assert.NotNull(await host.Files.Blobs.GetInfoAsync(blob.StorageKey, CancellationToken.None));
        await host.TrashAsync(other, second.Id);
        Assert.Equal(0, await host.PurgeAsync());
        Assert.Equal(0, await host.CollectAsync());

        host.Clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(1, await host.PurgeAsync());
        Assert.Equal(1, await host.CollectAsync());
        Assert.Null(await host.Files.Blobs.GetInfoAsync(blob.StorageKey, CancellationToken.None));
    }

    [PostgresFact]
    public async Task Orphan_grace_boundary_is_respected_and_staging_intents_are_preserved()
    {
        await using var host = await LibraryMaintenanceTestHost.CreateAsync();
        var owner = await host.CreateOwnerAsync();
        var asset = await host.ImportAsync(owner, Bytes("ready orphan"));
        var blob = await host.FindBlobAsync(asset.Id);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            await db.Assets.Where(item => item.Id == asset.Id).ExecuteDeleteAsync();
            await scope.ServiceProvider.GetRequiredService<IContentCatalog>().GetOrCreateBlobAsync(owner,
                new string('a', 64), 3, "application/octet-stream", host.Clock.GetUtcNow(), CancellationToken.None);
        }

        host.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await host.CollectAsync());
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await host.CollectAsync());
        Assert.Null(await host.Files.Blobs.GetInfoAsync(blob.StorageKey, CancellationToken.None));
        host.Clock.Advance(TimeSpan.FromDays(60));
        Assert.Equal(0, await host.CollectAsync());
        await using var verification = host.Services.CreateAsyncScope();
        var staging = await verification.ServiceProvider.GetRequiredService<NexoraDbContext>().Blobs.SingleAsync();
        Assert.Equal(BlobState.Staging, staging.State);
    }

    [PostgresFact]
    public async Task Purge_rechecks_the_asset_after_waiting_for_a_restore_transaction()
    {
        var barrier = new RestoreSaveBarrier();
        await using var host = await LibraryMaintenanceTestHost.CreateAsync(configure: services =>
            services.AddDbContext<NexoraDbContext>(options => options.AddInterceptors(barrier)));
        var owner = await host.CreateOwnerAsync();
        var asset = await host.ImportAsync(owner, Bytes("restore defeats a queued purge"));
        await host.TrashAsync(owner, asset.Id);
        host.Clock.Advance(TimeSpan.FromDays(31));
        barrier.Arm(asset.Id);

        await using var restoreScope = host.Services.CreateAsyncScope();
        var restore = restoreScope.ServiceProvider.GetRequiredService<IAssetLifecycle>()
            .RestoreAsync(owner, asset.Id, CancellationToken.None);
        var blockerPid = await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var purge = host.PurgeAsync();
        try
        {
            await WaitForBlockedSessionsAsync(host.Database.ConnectionString, blockerPid, 1);
        }
        finally
        {
            barrier.Release.TrySetResult();
        }

        Assert.NotNull(await restore.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await purge.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await host.CollectAsync());
        await using var verification = host.Services.CreateAsyncScope();
        var surviving = await verification.ServiceProvider.GetRequiredService<NexoraDbContext>().Assets.SingleAsync();
        Assert.Null(surviving.DeletedAt);
    }

    [PostgresFact]
    public async Task Restore_after_committed_purge_returns_missing_without_recreating_an_asset()
    {
        await using var host = await LibraryMaintenanceTestHost.CreateAsync();
        var owner = await host.CreateOwnerAsync();
        var asset = await host.ImportAsync(owner, Bytes("purge wins before restore"));
        await host.TrashAsync(owner, asset.Id);
        host.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, await host.PurgeAsync());

        await using var scope = host.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IAssetLifecycle>()
            .RestoreAsync(owner, asset.Id, CancellationToken.None));
        Assert.Equal(0, await host.CountAssetsAsync());
        Assert.Equal(1, await host.CollectAsync());
    }

    [PostgresFact]
    public async Task Concurrent_deduplication_and_collection_finish_without_deadlock_or_missing_content()
    {
        await using var host = await LibraryMaintenanceTestHost.CreateAsync();
        var owner = await host.CreateOwnerAsync();
        var bytes = Bytes("content races its orphan collection");
        var asset = await host.ImportAsync(owner, bytes);
        var oldBlob = await host.FindBlobAsync(asset.Id);
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Assets
                .Where(item => item.Id == asset.Id).ExecuteDeleteAsync();
        host.Clock.Advance(TimeSpan.FromDays(2));

        await using var barrierConnection = new NpgsqlConnection(host.Database.ConnectionString);
        await barrierConnection.OpenAsync();
        await using var transaction = await barrierConnection.BeginTransactionAsync();
        await using (var barrierCommand = barrierConnection.CreateCommand())
        {
            barrierCommand.CommandText = "SELECT pg_advisory_xact_lock(@key)";
            barrierCommand.Parameters.AddWithValue("key", BinaryPrimitives.ReadInt64BigEndian(
                Convert.FromHexString(oldBlob.Sha256.AsSpan(0, 16))));
            await barrierCommand.ExecuteNonQueryAsync();
        }

        var collection = host.CollectAsync();
        var import = host.ImportResultAsync(owner, bytes);
        try
        {
            await WaitForBlockedSessionsAsync(host.Database.ConnectionString, barrierConnection.ProcessID, 2);
        }
        finally
        {
            await transaction.CommitAsync();
        }
        await Task.WhenAll(collection, import).WaitAsync(TimeSpan.FromSeconds(20));

        var result = await import;
        Assert.True(result.Status is AssetImportStatus.Created or AssetImportStatus.Reused or AssetImportStatus.BlobUnavailable);
        // Collection may win after lookup and before reference creation. The caller
        // retries the identified unavailable result after that generation is gone.
        var survivingAsset = result.Asset ?? await host.ImportAsync(owner, bytes);
        var surviving = await host.FindBlobAsync(survivingAsset.Id);
        Assert.Equal(BlobState.Ready, surviving.State);
        Assert.Equal(1, await host.CountAssetsAsync());
        Assert.Equal(1, await host.CountBlobsAsync());
        await using var original = await host.Files.Blobs.OpenReadAsync(surviving.StorageKey, CancellationToken.None);
        using var returned = new MemoryStream();
        await original.CopyToAsync(returned);
        Assert.Equal(bytes, returned.ToArray());
        Assert.InRange(await collection, 0, 1);
    }

    [PostgresFact]
    public async Task Failure_after_physical_deletion_leaves_a_retryable_generation_and_reupload_uses_a_new_uuid()
    {
        DeleteAfterPublicationFailure? failing = null;
        await using var host = await LibraryMaintenanceTestHost.CreateAsync(configureWithFiles: (services, files) =>
        {
            failing = new DeleteAfterPublicationFailure(files.Blobs);
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage>(failing);
        });
        var owner = await host.CreateOwnerAsync();
        var bytes = Bytes("immutable generation survives delayed deletion attempts");
        var first = await host.ImportAsync(owner, bytes);
        var oldBlob = await host.FindBlobAsync(first.Id);
        await host.TrashAsync(owner, first.Id);
        host.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, await host.PurgeAsync());

        Assert.Equal(0, await host.CollectAsync());
        await using (var verification = host.Services.CreateAsyncScope())
        {
            var deleting = await verification.ServiceProvider.GetRequiredService<NexoraDbContext>().Blobs.SingleAsync();
            Assert.Equal(oldBlob.Id, deleting.Id);
            Assert.Equal(BlobState.Deleting, deleting.State);
        }
        Assert.Null(await host.Files.Blobs.GetInfoAsync(oldBlob.StorageKey, CancellationToken.None));
        Assert.Equal(1, await host.CollectAsync());
        Assert.Equal(0, await host.CollectAsync());

        var reupload = await host.ImportAsync(owner, bytes);
        var newBlob = await host.FindBlobAsync(reupload.Id);
        Assert.NotEqual(oldBlob.Id, newBlob.Id);
        Assert.Equal(oldBlob.Sha256, newBlob.Sha256);
        Assert.NotNull(failing);
        await failing.DeleteAsync(oldBlob.StorageKey, CancellationToken.None);
        Assert.NotNull(await host.Files.Blobs.GetInfoAsync(newBlob.StorageKey, CancellationToken.None));
        Assert.Equal(0, await host.CollectAsync());
        Assert.Equal(1, await host.CountAssetsAsync());
    }

    [PostgresFact]
    public async Task Collection_cleans_published_derivatives_abandoned_attempts_jobs_and_reservations()
    {
        await using var host = await LibraryMaintenanceTestHost.CreateAsync();
        var owner = await host.CreateOwnerAsync();
        var png = ImageTestHost.Encode(16, 10);
        var asset = await host.ImportAsync(owner, png);
        var blob = await host.FindBlobAsync(asset.Id);
        var work = await host.ClaimAsync();
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IImageJobProcessor>().ProcessAsync(work, CancellationToken.None);
        var abandoned = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            db.BackgroundJobAttempts.Add(new BackgroundJobAttempt(abandoned, work.Lease.JobId, host.Clock.GetUtcNow()));
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IDerivativeStorage>().PublishAsync(
                new DerivativeKey(abandoned, DerivativeKind.Thumbnail), png, CancellationToken.None);
            Assert.True((await db.BlobImages.SingleAsync()).ReservedBytes > 0);
        }
        await host.TrashAsync(owner, asset.Id);
        host.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, await host.PurgeAsync());
        Assert.Equal(1, await host.CollectAsync());

        await using var verification = host.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await context.BlobImages.CountAsync());
        Assert.Equal(0, await context.BackgroundJobs.CountAsync());
        Assert.Equal(0, await context.BackgroundJobAttempts.CountAsync());
        var derivatives = verification.ServiceProvider.GetRequiredService<IDerivativeStorage>();
        foreach (var generation in new[] { work.Lease.Token, abandoned })
            foreach (var kind in new[] { DerivativeKind.Thumbnail, DerivativeKind.Preview })
                Assert.Null(await derivatives.GetInfoAsync(new DerivativeKey(generation, kind), CancellationToken.None));
        Assert.Null(await host.Files.Blobs.GetInfoAsync(blob.StorageKey, CancellationToken.None));
    }

    [PostgresFact]
    public async Task Collection_does_not_wait_for_a_pending_image_that_cannot_reserve_capacity()
    {
        await using var host = await LibraryMaintenanceTestHost.CreateAsync(settings: new Dictionary<string, string?>
        {
            ["Uploads:MaximumReservedBytes"] = "1048576",
            ["Images:MaximumDerivativeBytes"] = "1048576"
        });
        var owner = await host.CreateOwnerAsync();
        var asset = await host.ImportAsync(owner, ImageTestHost.Encode(12, 7));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().ClaimAsync(CancellationToken.None));
            var job = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().BackgroundJobs.SingleAsync();
            Assert.Equal(BackgroundJobState.Pending, job.State);
            Assert.Equal(0, job.Attempts);
        }
        await host.TrashAsync(owner, asset.Id);
        host.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, await host.PurgeAsync());
        Assert.Equal(1, await host.CollectAsync());
        Assert.Equal(0, await host.CountBlobsAsync());
    }

    [PostgresFact]
    public async Task Running_image_lease_prevents_collection_until_it_expires()
    {
        await using var host = await LibraryMaintenanceTestHost.CreateAsync(settings: ShortRetention());
        var owner = await host.CreateOwnerAsync();
        var asset = await host.ImportAsync(owner, ImageTestHost.Encode(12, 7));
        var work = await host.ClaimAsync();
        await host.TrashAsync(owner, asset.Id);
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await host.PurgeAsync());

        Assert.Equal(0, await host.CollectAsync());
        Assert.NotNull(await host.Files.Blobs.GetInfoAsync(work.Blob.StorageKey, CancellationToken.None));
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await host.CollectAsync());
        Assert.Null(await host.Files.Blobs.GetInfoAsync(work.Blob.StorageKey, CancellationToken.None));
    }

    [PostgresFact]
    public async Task Expired_lease_does_not_allow_collection_while_an_image_processor_still_holds_its_guard()
    {
        var renderer = new BarrierImageRenderer(ImageTestHost.Encode(12, 7));
        await using var host = await LibraryMaintenanceTestHost.CreateAsync(settings: ShortRetention(), configure: services =>
        {
            services.RemoveAll<IImageRenderer>();
            services.AddSingleton<IImageRenderer>(renderer);
        });
        var owner = await host.CreateOwnerAsync();
        var asset = await host.ImportAsync(owner, ImageTestHost.Encode(12, 7));
        var work = await host.ClaimAsync();
        await using var processingScope = host.Services.CreateAsyncScope();
        var processing = processingScope.ServiceProvider.GetRequiredService<IImageJobProcessor>()
            .ProcessAsync(work, CancellationToken.None);
        await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await host.TrashAsync(owner, asset.Id);
            host.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(1, await host.PurgeAsync());
            Assert.Equal(0, await host.CollectAsync());
            Assert.NotNull(await host.Files.Blobs.GetInfoAsync(work.Blob.StorageKey, CancellationToken.None));
            Assert.False(processing.IsCompleted);
        }
        finally
        {
            renderer.Release.TrySetResult();
        }
        var error = await Record.ExceptionAsync(() => processing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(error is JobLeaseLostException or OperationCanceledException,
            "The expired processor must lose its lease before publishing or completing.");
        Assert.Equal(1, await host.CollectAsync());
        Assert.Null(await host.Files.Blobs.GetInfoAsync(work.Blob.StorageKey, CancellationToken.None));
    }

    [PostgresFact]
    public async Task Purged_upload_history_preserves_success_and_repeated_completion_cannot_recreate_the_asset()
    {
        var clock = new MutableTimeProvider();
        await using var host = await FileApiTestHost.CreateAsync(clock);
        var tokens = await host.LoginAsync();
        var bytes = Bytes("completed upload whose library result is eventually purged");
        var upload = await host.CreateUploadAsync(tokens.AccessToken, bytes);
        await host.SendChunksAsync(tokens.AccessToken, upload, bytes);
        using (var complete = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", tokens.AccessToken))
            Assert.Equal(HttpStatusCode.Accepted, complete.StatusCode);
        await host.ProcessOneAsync();
        var completed = await host.GetUploadAsync(tokens.AccessToken, upload.Id);
        Assert.Equal(UploadState.Completed, completed.State);
        Assert.NotNull(completed.Result);
        using (var trash = await host.SendAsync(HttpMethod.Delete, $"/api/assets/{completed.Result.Id}", tokens.AccessToken))
            Assert.Equal(HttpStatusCode.NoContent, trash.StatusCode);
        clock.Advance(TimeSpan.FromDays(31));
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IContentMaintenance>()
                .PurgeExpiredAssetsAsync(100, CancellationToken.None));

        var freshTokens = await host.LoginAsync();
        var history = await host.GetUploadAsync(freshTokens.AccessToken, upload.Id);
        Assert.Equal(UploadState.Completed, history.State);
        Assert.Null(history.Result);
        Assert.Null(history.FailureCode);
        Assert.Equal(clock.GetUtcNow(), history.ResultPurgedAt);
        Assert.NotNull(history.Operation);
        Assert.Equal(completed.Operation?.Id, history.Operation.Id);
        Assert.Equal(BackgroundJobState.Succeeded, history.Operation.State);
        using (var repeated = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", freshTokens.AccessToken))
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var again = await host.GetUploadAsync(freshTokens.AccessToken, upload.Id);
        Assert.Equal(history.ResultPurgedAt, again.ResultPurgedAt);
        Assert.Equal(history.Operation.Id, again.Operation?.Id);
        await using var verification = host.Factory.Services.CreateAsyncScope();
        Assert.Equal(0, await verification.ServiceProvider.GetRequiredService<NexoraDbContext>().Assets.CountAsync());
    }

    [PostgresFact]
    public async Task Losing_the_guard_connection_cancels_the_renderer_and_preserves_the_original()
    {
        var renderer = new BarrierImageRenderer(ImageTestHost.Encode(12, 7), observeCancellation: true);
        await using var host = await LibraryMaintenanceTestHost.CreateAsync(configure: services =>
        {
            services.RemoveAll<IImageRenderer>();
            services.AddSingleton<IImageRenderer>(renderer);
        });
        var owner = await host.CreateOwnerAsync();
        var asset = await host.ImportAsync(owner, ImageTestHost.Encode(12, 7));
        var work = await host.ClaimAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var processing = scope.ServiceProvider.GetRequiredService<IImageJobProcessor>().ProcessAsync(work, CancellationToken.None);
        await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // Terminate only the shared advisory session in this fixture's own GUID database.
            await using var observer = new NpgsqlConnection(host.Database.ConnectionString);
            await observer.OpenAsync();
            await using var command = observer.CreateCommand();
            command.CommandText = """
                SELECT pg_terminate_backend(activity.pid)
                FROM pg_stat_activity activity JOIN pg_locks locks ON locks.pid = activity.pid
                WHERE activity.datname = current_database() AND activity.pid <> pg_backend_pid()
                  AND locks.locktype = 'advisory' AND locks.mode = 'ShareLock'
                  AND locks.classid::bigint = 1314408781 AND locks.objsubid = 2
                """;
            Assert.Equal(true, await command.ExecuteScalarAsync());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.NotNull(await host.Files.Blobs.GetInfoAsync(work.Blob.StorageKey, CancellationToken.None));
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IDerivativeStorage>()
                .GetInfoAsync(new DerivativeKey(work.Lease.Token, DerivativeKind.Thumbnail), CancellationToken.None));
            Assert.Equal(1, await host.CountAssetsAsync());
        }
        finally { renderer.Release.TrySetResult(); }
    }

    [PostgresFact]
    public async Task Library_migration_can_revert_before_purge_and_reapply_without_losing_originals()
    {
        await using var host = await LibraryMaintenanceTestHost.CreateAsync();
        var owner = await host.CreateOwnerAsync();
        var asset = await host.ImportAsync(owner, Bytes("migration round trip preserves original"));
        var blob = await host.FindBlobAsync(asset.Id);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261008122015_ImageMetadataAndDerivatives");
        await migrator.MigrateAsync();
        Assert.True(await db.Assets.AnyAsync(item => item.Id == asset.Id));
        Assert.NotNull(await host.Files.Blobs.GetInfoAsync(blob.StorageKey, CancellationToken.None));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Library_migration_refuses_reversal_after_a_completed_upload_result_has_been_purged()
    {
        var clock = new MutableTimeProvider();
        await using var host = await FileApiTestHost.CreateAsync(clock);
        var tokens = await host.LoginAsync();
        var bytes = Bytes("purged history cannot be reversed safely");
        var upload = await host.CreateUploadAsync(tokens.AccessToken, bytes);
        await host.SendChunksAsync(tokens.AccessToken, upload, bytes);
        using (var response = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", tokens.AccessToken))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await host.ProcessOneAsync();
        var completed = await host.GetUploadAsync(tokens.AccessToken, upload.Id);
        Assert.NotNull(completed.Result);
        using (var response = await host.SendAsync(HttpMethod.Delete, $"/api/assets/{completed.Result.Id}", tokens.AccessToken))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        clock.Advance(TimeSpan.FromDays(31));
        await using var scope = host.Factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IContentMaintenance>()
            .PurgeExpiredAssetsAsync(100, CancellationToken.None));
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>()
            .MigrateAsync("20261008122015_ImageMetadataAndDerivatives"));
        Assert.Contains("restore a consistent backup", exception.MessageText);
        Assert.Contains("20261008125535_LibraryTrashAndPurge", await db.Database.GetAppliedMigrationsAsync());
        // Later migrations may already have been reverted before the library guard rejects.
        // Inspect the surviving library-era column without materializing the newest schema.
        Assert.NotNull(await db.UploadSessions.AsNoTracking().Where(item => item.Id == upload.Id)
            .Select(item => item.ResultPurgedAt).SingleAsync());
    }

    [Theory]
    [InlineData("Library:TrashRetention", "00:00:00")]
    [InlineData("Library:TrashRetention", "366.00:00:00")]
    [InlineData("Library:UnreferencedBlobGracePeriod", "-00:00:01")]
    [InlineData("Library:MaintenanceInterval", "31.00:00:00")]
    [InlineData("Library:MaintenanceBatchSize", "0")]
    [InlineData("Library:MaintenanceBatchSize", "1001")]
    public async Task Invalid_library_configuration_prevents_startup(string key, string value)
    {
        await using var factory = new ApiFactory(configuration: new Dictionary<string, string?> { [key] = value });
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        if (error is ObjectDisposedException)
        {
            Assert.Contains("OptionsValidationException", factory.CapturedLogs.Text);
            Assert.Contains(key, factory.CapturedLogs.Text);
        }
        else
        {
            Assert.Contains(key, error.ToString());
        }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static Dictionary<string, string?> ShortRetention() => new()
    {
        ["Library:TrashRetention"] = "00:00:01",
        ["Library:UnreferencedBlobGracePeriod"] = "00:00:01"
    };

    private static async Task WaitForBlockedSessionsAsync(string connectionString, int blockerPid, int count)
    {
        // The barrier depends on actual PostgreSQL lock waiters, not an assumed task scheduling delay.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync(timeout.Token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock'
              AND @blocker = ANY(pg_blocking_pids(pid))
            """;
        command.Parameters.AddWithValue("blocker", blockerPid);
        while (Convert.ToInt64(await command.ExecuteScalarAsync(timeout.Token)) < count)
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
    }

    private sealed class RestoreSaveBarrier : SaveChangesInterceptor
    {
        private Guid? _assetId;
        public TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm(Guid assetId) => _assetId = assetId;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            if (_assetId is { } id && db.ChangeTracker.Entries<Asset>()
                .Any(entry => entry.Entity.Id == id && entry.Entity.DeletedAt is null && entry.State == EntityState.Modified))
            {
                _assetId = null;
                Entered.TrySetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class DeleteAfterPublicationFailure(IBlobStorage actual) : IBlobStorage
    {
        private int _remainingFailures = 1;
        public Task<BlobPublicationResult> PublishAsync(BlobStorageKey key, Stream content, long expectedLength,
            string expectedSha256, CancellationToken cancellationToken) =>
            actual.PublishAsync(key, content, expectedLength, expectedSha256, cancellationToken);
        public Task<Stream> OpenReadAsync(BlobStorageKey key, CancellationToken cancellationToken) =>
            actual.OpenReadAsync(key, cancellationToken);
        public Task<BlobObjectInfo?> GetInfoAsync(BlobStorageKey key, CancellationToken cancellationToken) =>
            actual.GetInfoAsync(key, cancellationToken);
        public async Task DeleteAsync(BlobStorageKey key, CancellationToken cancellationToken)
        {
            await actual.DeleteAsync(key, cancellationToken);
            if (Interlocked.Exchange(ref _remainingFailures, 0) == 1)
                throw new IOException("Simulated failure after immutable generation deletion.");
        }
    }

    private sealed class BarrierImageRenderer(byte[] png, bool observeCancellation = false) : IImageRenderer
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RenderedImage> RenderAsync(BlobDescriptor blob, Stream original, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            // Simulate a renderer still unwinding after lease cancellation: collection must retain its guard.
            if (observeCancellation) await Release.Task.WaitAsync(cancellationToken);
            else await Release.Task;
            return new RenderedImage(12, 7, null, null, png, png);
        }
    }
}

internal sealed class LibraryMaintenanceTestHost : IAsyncDisposable
{
    private readonly IConfigurationRoot _configuration;
    private LibraryMaintenanceTestHost(PostgresTestDatabase database, StorageTestFiles files,
        IReadOnlyDictionary<string, string?>? settings, Action<IServiceCollection>? configure,
        Action<IServiceCollection, StorageTestFiles>? configureWithFiles)
    {
        Database = database;
        Files = files;
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] = database.ConnectionString,
            ["Storage:RootPath"] = files.RootPath,
            ["Uploads:MaximumReservedBytes"] = "67108864",
            ["Uploads:MinimumFreeBytes"] = "0",
            ["Images:MaximumDerivativeBytes"] = "1048576"
        };
        if (settings is not null) foreach (var entry in settings) values[entry.Key] = entry.Value;
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddLogging().AddInfrastructure(_configuration);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(Clock);
        configure?.Invoke(services);
        configureWithFiles?.Invoke(services, files);
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public PostgresTestDatabase Database { get; }
    public StorageTestFiles Files { get; }
    public ServiceProvider Services { get; }
    public MutableTimeProvider Clock { get; } = new();

    public static async Task<LibraryMaintenanceTestHost> CreateAsync(IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configure = null, Action<IServiceCollection, StorageTestFiles>? configureWithFiles = null)
    {
        var database = await PostgresTestDatabase.CreateAsync();
        var files = new StorageTestFiles();
        LibraryMaintenanceTestHost? host = null;
        try
        {
            host = new LibraryMaintenanceTestHost(database, files, settings, configure, configureWithFiles);
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.MigrateAsync();
            return host;
        }
        catch
        {
            if (host is not null)
            {
                await host.Services.DisposeAsync();
                (host._configuration as IDisposable)?.Dispose();
            }
            files.Dispose();
            await database.DisposeAsync();
            throw;
        }
    }

    public async Task<Guid> CreateOwnerAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var id = Guid.NewGuid();
        var email = id.ToString("N") + "@nexora.test";
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<NexoraUser>>().CreateAsync(
            new NexoraUser { Id = id, UserName = email, Email = email }, "Test-owner-password-8612!");
        Assert.True(result.Succeeded);
        return id;
    }

    public async Task<AssetSnapshot> ImportAsync(Guid owner, byte[] bytes)
    {
        var result = await ImportResultAsync(owner, bytes);
        Assert.Equal(AssetImportStatus.Created, result.Status);
        Assert.NotNull(result.Asset);
        return result.Asset;
    }

    public async Task<AssetImportResult> ImportResultAsync(Guid owner, byte[] bytes)
    {
        await using var scope = Services.CreateAsyncScope();
        await using var stream = new MemoryStream(bytes, writable: false);
        var result = await scope.ServiceProvider.GetRequiredService<IAssetIngestionService>().ImportAsync(
            new AssetImportRequest(owner, "private-library-item.bin", bytes.Length), stream, CancellationToken.None);
        return result;
    }

    public async Task TrashAsync(Guid owner, Guid assetId)
    {
        await using var scope = Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IAssetLifecycle>()
            .MoveToTrashAsync(owner, assetId, CancellationToken.None));
    }

    public async Task<BlobDescriptor> FindBlobAsync(Guid assetId)
    {
        await using var scope = Services.CreateAsyncScope();
        var blob = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Assets.AsNoTracking()
            .Where(asset => asset.Id == assetId).Select(asset => asset.Blob).SingleAsync();
        return new BlobDescriptor(blob.Id, blob.Sha256, blob.Size, blob.DetectedMimeType, blob.StorageKey, blob.State);
    }

    public async Task<ImageWorkItem> ClaimAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var work = await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().ClaimAsync(CancellationToken.None);
        Assert.NotNull(work);
        return work;
    }

    public async Task<int> PurgeAsync(int limit = 100)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IContentMaintenance>()
            .PurgeExpiredAssetsAsync(limit, CancellationToken.None);
    }

    public async Task<int> CollectAsync(int limit = 100)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IContentMaintenance>()
            .CollectBlobsAsync(limit, CancellationToken.None);
    }

    public async Task<int> CountAssetsAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Assets.CountAsync();
    }

    public async Task<int> CountBlobsAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Blobs.CountAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        (_configuration as IDisposable)?.Dispose();
        Files.Dispose();
        await Database.DisposeAsync();
    }
}
