using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Content;
using Nexora.Domain.Content;
using Nexora.Domain.Images;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class SyncApiTests
{
    [PostgresFact]
    public async Task Snapshot_pages_preserve_the_initial_library_while_mutations_and_purge_are_replayed_afterward()
    {
        var clock = new MutableTimeProvider();
        await using var host = await ImageTestHost.CreateAsync(clock);
        var token = await host.LoginAsync();
        var original = new List<AssetSnapshot>();
        for (byte item = 1; item <= 3; item++) original.Add(await host.ImportAsync([item], $"item-{item}.bin"));
        var first = await SnapshotAsync(host, token, "?limit=1");
        Assert.NotNull(first.NextCursor);
        var remaining = original.Where(asset => asset.Id != first.Items[0].Id).ToArray();
        var purge = remaining[0];
        var update = remaining[1];
        using (var rename = await host.SendContentAsync(HttpMethod.Patch, $"/api/assets/{update.Id}", token,
            JsonContent.Create(new { originalName = "renamed-after-snapshot.bin", isFavorite = true })))
            Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        using (var trash = await host.SendAsync(HttpMethod.Delete, $"/api/assets/{purge.Id}", token))
            Assert.Equal(HttpStatusCode.NoContent, trash.StatusCode);
        clock.Advance(TimeSpan.FromDays(31));
        token = await host.LoginAsync();
        // Purge bypasses tracked HTTP updates, but its tombstone belongs to the same journal.
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IContentMaintenance>()
                .PurgeExpiredAssetsAsync(100, CancellationToken.None));
        var imported = await host.ImportAsync([44], "created-after-snapshot.bin");
        var snapshotItems = first.Items.ToList();
        var snapshotCursor = first.NextCursor;
        while (snapshotCursor is not null)
        {
            var page = await SnapshotAsync(host, token, "?limit=1&cursor=" + snapshotCursor);
            snapshotItems.AddRange(page.Items);
            snapshotCursor = page.NextCursor;
        }
        Assert.Equal(original.OrderBy(item => item.Id), snapshotItems.OrderBy(item => item.Id));
        var cache = snapshotItems.ToDictionary(asset => asset.Id);
        var cursor = first.Cursor;
        var changes = new List<SyncChange>();
        bool more;
        do
        {
            var page = await ChangesAsync(host, token, cursor, 1);
            changes.AddRange(page.Items);
            foreach (var change in page.Items)
                if (change.Kind == "purge") cache.Remove(change.AssetId); else cache[change.AssetId] = Assert.IsType<AssetSnapshot>(change.Asset);
            cursor = page.Cursor;
            more = page.HasMore;
        } while (more);
        Assert.Equal(4, changes.Count);
        Assert.Equal("purge", changes.Single(change => change.AssetId == purge.Id && change.Asset is null).Kind);
        Assert.Contains(imported.Id, cache.Keys);
        Assert.Equal("renamed-after-snapshot.bin", cache[update.Id].OriginalName);
        Assert.Equal((await SnapshotAsync(host, token)).Items.OrderBy(item => item.Id), cache.Values.OrderBy(item => item.Id));
        Assert.Empty((await ChangesAsync(host, token, cursor)).Items);
    }

    [PostgresFact]
    public async Task Shared_image_completion_and_trash_restore_are_full_owner_scoped_upserts()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var second = await host.CreateSecondOwnerAsync();
        var bytes = ImageTestHost.Encode(24, 12);
        var asset = await host.ImportAsync(bytes);
        var other = await host.ImportAsync(bytes, ownerId: second.Id);
        var start = await SnapshotAsync(host, token);
        var otherStart = await SnapshotAsync(host, second.Token);
        Assert.Equal(ImageProcessingState.Pending, Assert.Single(start.Items).Image?.State);
        await host.ProcessAsync(await host.ClaimAsync());
        await host.CleanupAsync();
        var page = await ChangesAsync(host, token, start.Cursor);
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, change => Assert.Equal(asset.Id, change.AssetId));
        var ready = Assert.IsType<AssetSnapshot>(page.Items[^1].Asset);
        Assert.Equal(ImageProcessingState.Ready, ready.Image?.State);
        Assert.Equal(24, ready.Image?.Width);
        Assert.True(ready.Image?.HasThumbnail);
        var secondPage = await ChangesAsync(host, second.Token, otherStart.Cursor);
        Assert.All(secondPage.Items, change => Assert.Equal(other.Id, change.AssetId));
        Assert.Equal(ImageProcessingState.Ready, secondPage.Items[^1].Asset?.Image?.State);
        using (var trash = await host.SendAsync(HttpMethod.Delete, $"/api/assets/{asset.Id}", token))
            Assert.Equal(HttpStatusCode.NoContent, trash.StatusCode);
        var deleted = await ChangesAsync(host, token, page.Cursor);
        Assert.NotNull(Assert.Single(deleted.Items).Asset?.DeletedAt);
        Assert.NotNull(Assert.Single((await SnapshotAsync(host, token)).Items).DeletedAt);
        using (var restore = await host.SendAsync(HttpMethod.Post, $"/api/trash/{asset.Id}/restore", token))
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var restored = Assert.Single((await ChangesAsync(host, token, deleted.Cursor)).Items);
        Assert.Null(restored.Asset?.DeletedAt);
        Assert.Equal(ImageProcessingState.Ready, restored.Asset?.Image?.State);
        Assert.Empty((await ChangesAsync(host, second.Token, secondPage.Cursor)).Items);
    }

    [PostgresFact]
    public async Task Cursors_require_authentication_and_bind_owner_purpose_and_integrity_with_bounded_validation()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var second = await host.CreateSecondOwnerAsync();
        await host.ImportAsync([10]);
        await host.ImportAsync([11]);
        var start = await SnapshotAsync(host, token, "?limit=1");
        foreach (var path in new[] { "/api/sync", "/api/sync/changes?cursor=" + start.Cursor })
        {
            using var anonymous = await host.SendAsync(HttpMethod.Get, path, null);
            await AuthenticationTestHost.AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "authentication_required");
        }
        foreach (var path in new[]
        {
            "/api/sync/changes", "/api/sync?cursor=" + start.Cursor,
            "/api/sync/changes?cursor=" + start.NextCursor,
            "/api/sync/changes?cursor=" + start.Cursor[..^8] + "AAAAAAAA",
            "/api/sync?cursor=" + new string('a', 1025), "/api/sync?cursor=bad%20cursor"
        })
        {
            using var invalid = await host.SendAsync(HttpMethod.Get, path, token);
            await AuthenticationTestHost.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "invalid_cursor");
        }
        using (var foreign = await host.SendAsync(HttpMethod.Get, "/api/sync/changes?cursor=" + start.Cursor, second.Token))
            await AuthenticationTestHost.AssertProblemAsync(foreign, HttpStatusCode.BadRequest, "invalid_cursor");
        foreach (var limit in new[] { "0", "101", "-1", "invalid" })
        {
            using var invalid = await host.SendAsync(HttpMethod.Get, "/api/sync?limit=" + limit, token);
            await AuthenticationTestHost.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "invalid_request");
        }
        using var successful = await host.SendAsync(HttpMethod.Get, "/api/sync", token);
        Assert.Contains("no-store", successful.Headers.CacheControl?.ToString());
        var json = await successful.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerId", json, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Epoch_rotation_and_restored_counter_rollback_require_a_fresh_snapshot()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        await host.ImportAsync([12]);
        var start = await SnapshotAsync(host, token);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AssetSyncStates" SET "Sequence" = 0 WHERE "OwnerId" = {host.OwnerId}
                """);
        using (var reset = await host.SendAsync(HttpMethod.Get, "/api/sync/changes?cursor=" + start.Cursor, token))
            await AuthenticationTestHost.AssertProblemAsync(reset, HttpStatusCode.Conflict, "sync_reset_required");
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AssetSyncStates" SET "Sequence" = 1, "Epoch" = gen_random_uuid() WHERE "OwnerId" = {host.OwnerId}
                """);
        using (var reset = await host.SendAsync(HttpMethod.Get, "/api/sync/changes?cursor=" + start.Cursor, token))
            await AuthenticationTestHost.AssertProblemAsync(reset, HttpStatusCode.Conflict, "sync_reset_required");
        var fresh = await SnapshotAsync(host, token);
        Assert.Single(fresh.Items);
        Assert.Empty((await ChangesAsync(host, token, fresh.Cursor)).Items);
    }

    [PostgresFact]
    public async Task Journal_counter_serializes_commits_and_rolled_back_changes_never_advance_a_client()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var first = await host.ImportAsync([14], "first.bin");
        var second = await host.ImportAsync([15], "second.bin");
        var initial = await SnapshotAsync(host, token);
        await using var firstScope = host.Factory.Services.CreateAsyncScope();
        await using var secondScope = host.Factory.Services.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        await using var firstTransaction = await firstDb.Database.BeginTransactionAsync();
        await firstDb.Assets.Where(asset => asset.Id == first.Id).ExecuteUpdateAsync(update =>
            update.SetProperty(asset => asset.OriginalName, "first-committed.bin"));
        await using var secondTransaction = await secondDb.Database.BeginTransactionAsync();
        var secondPid = await secondDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var pending = secondDb.Assets.Where(asset => asset.Id == second.Id).ExecuteUpdateAsync(update =>
            update.SetProperty(asset => asset.OriginalName, "second-committed.bin"));
        try
        {
            var blocked = false;
            for (var attempt = 0; attempt < 100 && !blocked; attempt++)
            {
                blocked = await firstDb.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({secondPid})) > 0 AS \"Value\"").SingleAsync();
                if (!blocked) await Task.Delay(20);
            }
            Assert.True(blocked);
            Assert.Empty((await ChangesAsync(host, token, initial.Cursor).WaitAsync(TimeSpan.FromSeconds(5))).Items);
        }
        finally { await firstTransaction.CommitAsync(); }
        Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await secondTransaction.CommitAsync();
        var committed = await ChangesAsync(host, token, initial.Cursor, 1);
        Assert.True(committed.HasMore);
        Assert.Equal(first.Id, Assert.Single(committed.Items).AssetId);
        var next = await ChangesAsync(host, token, committed.Cursor);
        Assert.Equal(second.Id, Assert.Single(next.Items).AssetId);
        Assert.Equal(committed.Items[0].Sequence + 1, next.Items[0].Sequence);
        await using (var rollbackScope = host.Factory.Services.CreateAsyncScope())
        {
            var db = rollbackScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Assets.Where(asset => asset.Id == first.Id).ExecuteUpdateAsync(update => update.SetProperty(asset => asset.IsFavorite, true));
            await transaction.RollbackAsync();
        }
        Assert.Empty((await ChangesAsync(host, token, next.Cursor)).Items);
    }

    [PostgresFact]
    public async Task Migration_seeds_preexisting_assets_and_can_be_reapplied_without_losing_current_metadata()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var asset = await host.ImportAsync([17], "preexisting.bin");
        using (var trash = await host.SendAsync(HttpMethod.Delete, $"/api/assets/{asset.Id}", token))
            Assert.Equal(HttpStatusCode.NoContent, trash.StatusCode);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261008125535_LibraryTrashAndPurge");
            await migrator.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
        }
        var seeded = Assert.Single((await SnapshotAsync(host, token)).Items);
        Assert.Equal(asset.Id, seeded.Id);
        Assert.NotNull(seeded.DeletedAt);
        using (var restore = await host.SendAsync(HttpMethod.Post, $"/api/trash/{asset.Id}/restore", token))
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Null(Assert.Single((await SnapshotAsync(host, token)).Items).DeletedAt);
    }

    private static async Task<SyncSnapshotPage> SnapshotAsync(ImageTestHost host, string token, string query = "")
    {
        using var response = await host.SendAsync(HttpMethod.Get, "/api/sync" + query, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<SyncSnapshotPage>(await response.Content.ReadFromJsonAsync<SyncSnapshotPage>());
    }

    private static async Task<SyncChangesPage> ChangesAsync(ImageTestHost host, string token, string cursor, int limit = 50)
    {
        using var response = await host.SendAsync(HttpMethod.Get, $"/api/sync/changes?limit={limit}&cursor={cursor}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<SyncChangesPage>(await response.Content.ReadFromJsonAsync<SyncChangesPage>());
    }
}
