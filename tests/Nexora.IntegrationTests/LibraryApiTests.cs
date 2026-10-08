using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Content;
using Nexora.Domain.Images;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class LibraryApiTests
{
    [PostgresFact]
    public async Task Library_mutations_trash_and_restore_require_authentication_and_the_owner()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var owner = await host.LoginAsync();
        var other = await host.CreateSecondOwnerAsync();
        var first = await host.ImportAsync([7, 8, 9], "first.bin");
        var shared = await host.ImportAsync([7, 8, 9], "second.bin", other.Id);
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/trash"), (HttpMethod.Delete, $"/api/assets/{first.Id}"),
            (HttpMethod.Patch, $"/api/assets/{first.Id}"), (HttpMethod.Post, $"/api/trash/{first.Id}/restore")
        })
        {
            using var anonymous = await host.SendAsync(method, path, null,
                request => { if (method == HttpMethod.Patch) request.Content = JsonContent.Create(new { isFavorite = true }); });
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            if (method == HttpMethod.Get) continue;
            using var foreign = await host.SendAsync(method, path, other.Token,
                request => { if (method == HttpMethod.Patch) request.Content = JsonContent.Create(new { isFavorite = true }); });
            await AuthenticationTestHost.AssertProblemAsync(foreign, HttpStatusCode.NotFound, "resource_not_found");
        }
        using (var deleted = await host.SendAsync(HttpMethod.Delete, $"/api/assets/{first.Id}", owner))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await PageAsync(host, "/api/trash", other.Token)).Items);
        Assert.Equal(shared.Id, Assert.Single((await PageAsync(host, "/api/assets", other.Token)).Items).Id);
        Assert.Equal(first.Id, Assert.Single((await PageAsync(host, "/api/trash", owner)).Items).Id);
    }

    [PostgresFact]
    public async Task Patch_validates_payload_and_rename_updates_download_without_changing_content_or_upload_date()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var bytes = new byte[] { 1, 4, 8, 16 };
        var imported = await host.ImportAsync(bytes, "private-original.bin");
        var original = await host.GetAssetAsync(imported.Id, token);
        foreach (var body in new[] { "{}", "null", "[]", "{\"originalName\":null}", "{\"isFavorite\":null}",
            "{\"isFavorite\":\"true\"}", "{\"extra\":1}", "{\"originalName\":\"../escape\"}",
            "{\"isFavorite\":true,\"isFavorite\":false}", "{\"originalName\":\"\"}" })
        {
            using var invalid = await host.SendContentAsync(HttpMethod.Patch, $"/api/assets/{original.Id}", token,
                new StringContent(body, Encoding.UTF8, "application/json"));
            await AuthenticationTestHost.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "invalid_request");
        }
        using (var absent = await host.SendAsync(HttpMethod.Patch, $"/api/assets/{original.Id}", token))
            Assert.Equal(HttpStatusCode.BadRequest, absent.StatusCode);
        using (var updated = await host.SendContentAsync(HttpMethod.Patch, $"/api/assets/{original.Id}", token,
            JsonContent.Create(new { originalName = "private-renamed.bin", isFavorite = true })))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var snapshot = await ReadAsync<AssetSnapshot>(updated);
            Assert.Equal(original.Id, snapshot.Id);
            Assert.Equal(original.UploadedAt, snapshot.UploadedAt);
            Assert.True(snapshot.IsFavorite);
            Assert.Equal("private-renamed.bin", snapshot.OriginalName);
        }
        using (var favorite = await host.SendContentAsync(HttpMethod.Patch, $"/api/assets/{original.Id}", token,
            JsonContent.Create(new { isFavorite = false })))
        {
            var snapshot = await ReadAsync<AssetSnapshot>(favorite);
            Assert.False(snapshot.IsFavorite);
            Assert.Equal("private-renamed.bin", snapshot.OriginalName);
        }
        using var content = await host.SendAsync(HttpMethod.Get, $"/api/assets/{original.Id}/content", token);
        Assert.Equal(bytes, await content.Content.ReadAsByteArrayAsync());
        Assert.Contains("private-renamed.bin", content.Content.Headers.ContentDisposition?.ToString());
        var repeat = await host.ImportAsync(bytes, "reupload-name.bin");
        Assert.Equal("private-renamed.bin", repeat.OriginalName);
        Assert.DoesNotContain("private-renamed.bin", host.Factory.CapturedLogs.Text, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Trash_is_idempotent_hides_original_and_derivatives_and_requires_explicit_restore_for_reupload()
    {
        var clock = new MutableTimeProvider();
        await using var host = await ImageTestHost.CreateAsync(clock);
        var token = await host.LoginAsync();
        var original = ImageTestHost.Encode(32, 16);
        var asset = await host.ImportAsync(original);
        await host.ProcessAsync(await host.ClaimAsync());
        using (var favorite = await host.SendContentAsync(HttpMethod.Patch, $"/api/assets/{asset.Id}", token,
            JsonContent.Create(new { isFavorite = true }))) Assert.Equal(HttpStatusCode.OK, favorite.StatusCode);
        using (var first = await host.SendAsync(HttpMethod.Delete, $"/api/assets/{asset.Id}", token))
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var trashed = Assert.Single((await PageAsync(host, "/api/trash", token)).Items);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using (var repeated = await host.SendAsync(HttpMethod.Delete, $"/api/assets/{asset.Id}", token))
            Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(trashed.DeletedAt, Assert.Single((await PageAsync(host, "/api/trash", token)).Items).DeletedAt);
        Assert.Empty((await PageAsync(host, "/api/assets", token)).Items);
        foreach (var route in new[] { "", "/content", "/thumbnail", "/preview" })
        {
            using var hidden = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}{route}", token);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        using (var patch = await host.SendContentAsync(HttpMethod.Patch, $"/api/assets/{asset.Id}", token,
            JsonContent.Create(new { isFavorite = false }))) Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            await using var stream = new MemoryStream(original, false);
            var result = await scope.ServiceProvider.GetRequiredService<IAssetIngestionService>()
                .ImportAsync(new AssetImportRequest(host.OwnerId, "reupload.png", original.Length), stream, CancellationToken.None);
            Assert.Equal(AssetImportStatus.AssetInTrash, result.Status);
            Assert.Equal(asset.Id, result.Asset?.Id);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var restore = await host.SendAsync(HttpMethod.Post, $"/api/trash/{asset.Id}/restore", token);
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
            var restored = await ReadAsync<AssetSnapshot>(restore);
            Assert.Null(restored.DeletedAt);
            Assert.True(restored.IsFavorite);
            Assert.Equal(ImageProcessingState.Ready, restored.Image?.State);
        }
        using var content = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", token);
        Assert.Equal(original, await content.Content.ReadAsByteArrayAsync());
        using var derivative = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/thumbnail", token);
        Assert.Equal(HttpStatusCode.OK, derivative.StatusCode);
        Assert.Empty((await PageAsync(host, "/api/trash", token)).Items);
    }

    [PostgresFact]
    public async Task Filters_select_pending_images_and_favorites_and_validate_timeline_contract()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var first = await host.ImportAsync(ImageTestHost.Encode(32, 16), "first.png");
        var second = await host.ImportAsync(ImageTestHost.Encode(33, 16), "second.png");
        var plain = await host.ImportAsync([10, 11], "plain.bin");
        foreach (var asset in new[] { first, plain })
        {
            using var favorite = await host.SendContentAsync(HttpMethod.Patch, $"/api/assets/{asset.Id}", token,
                JsonContent.Create(new { isFavorite = true }));
            Assert.Equal(HttpStatusCode.OK, favorite.StatusCode);
        }
        Assert.Equal(2, (await PageAsync(host, "/api/assets?imagesOnly=true", token)).Items.Count);
        Assert.Equal(first.Id, Assert.Single((await PageAsync(host, "/api/assets?imagesOnly=true&isFavorite=true", token)).Items).Id);
        Assert.Equal(second.Id, Assert.Single((await PageAsync(host, "/api/assets?isFavorite=false", token)).Items).Id);
        Assert.Equal(2, (await PageAsync(host, "/api/assets?isFavorite=true", token)).Items.Count);
        Assert.Equal(2, (await PageAsync(host, "/api/assets?sort=timeline&imagesOnly=true", token)).Items.Count);
        foreach (var query in new[] { "sort=timeline", "sort=timeline&imagesOnly=false", "sort=unknown", "imagesOnly=wrong", "isFavorite=wrong" })
        {
            using var invalid = await host.SendAsync(HttpMethod.Get, "/api/assets?" + query, token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Cursors_bind_filters_owner_and_scope_and_keep_uuid_ties_in_postgresql_order()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var other = await host.CreateSecondOwnerAsync();
        var assets = new List<AssetSnapshot>();
        for (byte index = 1; index <= 4; index++) assets.Add(await host.ImportAsync([index], $"tie-{index}.bin"));
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var rows = await db.Assets.ToListAsync();
            foreach (var row in rows)
            {
                db.Entry(row).Property(item => item.UploadedAt).CurrentValue = assets[0].UploadedAt;
                row.SetFavorite(true);
            }
            await db.SaveChangesAsync();
        }
        var first = await PageAsync(host, "/api/assets?limit=1&isFavorite=true", token);
        Assert.NotNull(first.NextCursor);
        foreach (var path in new[] { "/api/assets", "/api/assets?isFavorite=false", "/api/assets?isFavorite=true&imagesOnly=true", "/api/trash" })
        {
            using var changed = await host.SendAsync(HttpMethod.Get, path + (path.Contains('?') ? "&" : "?") + "cursor=" + first.NextCursor, token);
            await AuthenticationTestHost.AssertProblemAsync(changed, HttpStatusCode.BadRequest, "invalid_cursor");
        }
        using (var foreign = await host.SendAsync(HttpMethod.Get, "/api/assets?isFavorite=true&cursor=" + first.NextCursor, other.Token))
            await AuthenticationTestHost.AssertProblemAsync(foreign, HttpStatusCode.BadRequest, "invalid_cursor");
        var ids = first.Items.Select(item => item.Id).ToList();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await PageAsync(host, "/api/assets?limit=1&isFavorite=true&cursor=" + cursor, token);
            ids.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        }
        Assert.Equal(assets.Select(item => item.Id).OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal), ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [PostgresFact]
    public async Task Timeline_uses_reliable_utc_capture_and_falls_back_to_upload_for_local_only_metadata()
    {
        var clock = new MutableTimeProvider();
        await using var host = await ImageTestHost.CreateAsync(clock);
        var token = await host.LoginAsync();
        var first = await host.ImportAsync(ImageTestHost.Encode(31, 16));
        var second = await host.ImportAsync(ImageTestHost.Encode(32, 16));
        var localOnly = await host.ImportAsync(ImageTestHost.Encode(33, 16));
        var local = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Unspecified);
        var utcFromMinusThree = new DateTimeOffset(local, TimeSpan.FromHours(-3)).ToUniversalTime();
        var utcFromPlusTwo = new DateTimeOffset(local, TimeSpan.FromHours(2)).ToUniversalTime();
        await SetCaptureAsync(host, first.Id, local, utcFromMinusThree, clock.GetUtcNow());
        await SetCaptureAsync(host, second.Id, local, utcFromPlusTwo, clock.GetUtcNow());
        await SetCaptureAsync(host, localOnly.Id, local.AddYears(70), null, clock.GetUtcNow());
        var page = await PageAsync(host, "/api/assets?imagesOnly=true&sort=timeline", token);
        Assert.Equal(new[] { localOnly.Id, first.Id, second.Id }, page.Items.Select(item => item.Id));
        Assert.Equal(utcFromMinusThree, page.Items.Single(item => item.Id == first.Id).Image?.CapturedAtUtc);
        Assert.Null(page.Items[0].Image?.CapturedAtUtc);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var image = await db.Assets.Where(item => item.Id == second.Id).Select(item => item.Blob.Image!).SingleAsync();
            db.Entry(image).Property(item => item.CapturedAtUtc).CurrentValue = utcFromMinusThree;
            await db.SaveChangesAsync();
        }
        var ties = new List<Guid>();
        string? cursor = null;
        do
        {
            var tiedPage = await PageAsync(host, "/api/assets?imagesOnly=true&sort=timeline&limit=1" +
                (cursor is null ? "" : "&cursor=" + cursor), token);
            ties.Add(Assert.Single(tiedPage.Items).Id);
            cursor = tiedPage.NextCursor;
        } while (cursor is not null);
        Assert.Equal(new[] { localOnly.Id }.Concat(new[] { first.Id, second.Id }
            .OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal)), ties);
    }

    [PostgresFact]
    public async Task Page_watermark_excludes_new_uploads_and_keeps_late_exif_processing_from_reordering_remaining_items()
    {
        var clock = new MutableTimeProvider();
        await using var host = await ImageTestHost.CreateAsync(clock);
        var token = await host.LoginAsync();
        var assets = new List<AssetSnapshot>();
        for (var index = 0; index < 3; index++)
        {
            assets.Add(await host.ImportAsync(ImageTestHost.Encode(31 + index, 16)));
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        var page = await PageAsync(host, "/api/assets?imagesOnly=true&sort=timeline&limit=1", token);
        Assert.Equal(assets[2].Id, Assert.Single(page.Items).Id);
        Assert.NotNull(page.NextCursor);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await SetCaptureAsync(host, assets[1].Id, new DateTime(2050, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTimeOffset(2050, 1, 1, 0, 0, 0, TimeSpan.Zero), clock.GetUtcNow());
        var newUpload = await host.ImportAsync(ImageTestHost.Encode(60, 16));
        var rest = await PageAsync(host, "/api/assets?imagesOnly=true&sort=timeline&limit=10&cursor=" + page.NextCursor, token);
        Assert.Equal(new[] { assets[1].Id, assets[0].Id }, rest.Items.Select(item => item.Id));
        Assert.DoesNotContain(newUpload.Id, rest.Items.Select(item => item.Id));
        Assert.Null(rest.NextCursor);
        var fresh = await PageAsync(host, "/api/assets?imagesOnly=true&sort=timeline", token);
        Assert.Equal(assets[1].Id, fresh.Items[0].Id);
        Assert.Contains(newUpload.Id, fresh.Items.Select(item => item.Id));
    }

    [PostgresFact]
    public async Task Trash_pages_use_deleted_timestamp_and_uuid_and_restore_missing_or_unready_assets_returns_not_found()
    {
        var clock = new MutableTimeProvider();
        await using var host = await ImageTestHost.CreateAsync(clock);
        var token = await host.LoginAsync();
        var assets = new List<AssetSnapshot>();
        for (byte index = 1; index <= 3; index++)
        {
            var asset = await host.ImportAsync([index]);
            assets.Add(asset);
            using var deleted = await host.SendAsync(HttpMethod.Delete, $"/api/assets/{asset.Id}", token);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await PageAsync(host, "/api/trash?limit=1" + (cursor is null ? "" : "&cursor=" + cursor), token);
            ids.Add(Assert.Single(page.Items).Id);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(assets.Select(item => item.Id).OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal), ids);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var rows = await db.Assets.ToListAsync();
            foreach (var row in rows)
            {
                db.Entry(row).Property(item => item.UploadedAt).CurrentValue = clock.GetUtcNow().AddDays(-2);
                row.Restore();
                row.MoveToTrash(clock.GetUtcNow().AddDays(-1).AddHours(row.Id == assets[1].Id ? 1 : 2));
            }
            await db.SaveChangesAsync();
        }
        var dates = await PageAsync(host, "/api/trash", token);
        Assert.Equal(new[] { assets[0].Id, assets[2].Id }.OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal)
            .Append(assets[1].Id), dates.Items.Select(item => item.Id));
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var missing = await db.Assets.SingleAsync(item => item.Id == assets[0].Id);
            db.Assets.Remove(missing);
            var unready = await db.Assets.Include(item => item.Blob).SingleAsync(item => item.Id == assets[1].Id);
            unready.Blob.MarkDeleting();
            await db.SaveChangesAsync();
        }
        foreach (var id in new[] { assets[0].Id, assets[1].Id, Guid.NewGuid() })
        {
            using var unavailable = await host.SendAsync(HttpMethod.Post, $"/api/trash/{id}/restore", token);
            await AuthenticationTestHost.AssertProblemAsync(unavailable, HttpStatusCode.NotFound, "resource_not_found");
        }
    }

    private static async Task SetCaptureAsync(ImageTestHost host, Guid assetId, DateTime local, DateTimeOffset? utc, DateTimeOffset processedAt)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var blobId = await db.Assets.Where(item => item.Id == assetId).Select(item => item.BlobId).SingleAsync();
        var image = await db.BlobImages.SingleAsync(item => item.BlobId == blobId);
        image.BeginProcessing(100);
        image.Complete(Guid.NewGuid(), 32, 16, local, utc, 1, new string('a', 64), 1, new string('b', 64), processedAt);
        image.FinishCleanup();
        await db.SaveChangesAsync();
    }

    private static async Task<AssetPage> PageAsync(ImageTestHost host, string path, string token)
    {
        using var response = await host.SendAsync(HttpMethod.Get, path, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<AssetPage>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var result = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(result);
        return result;
    }
}
