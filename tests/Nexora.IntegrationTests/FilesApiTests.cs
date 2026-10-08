using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Content;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Application.Uploads;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class FilesApiTests
{
    [PostgresFact]
    public async Task ChunksCanArriveOutOfOrderAndCompletionProducesAnAttachmentWithRangeAndConditionalRequests()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var tokens = await host.LoginAsync();
        var bytes = Enumerable.Range(0, 37).Select(value => (byte)value).ToArray();
        const string name = "private-download-example.bin";
        var upload = await host.CreateUploadAsync(tokens.AccessToken, bytes, name);
        Assert.Equal(3, upload.ChunkCount);
        using (var last = await host.PutAsync(tokens.AccessToken, upload.Id, 2, bytes[32..]))
            Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        using (var first = await host.PutAsync(tokens.AccessToken, upload.Id, 0, bytes[..16]))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var repeat = await host.PutAsync(tokens.AccessToken, upload.Id, 0, bytes[..16]))
        {
            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
            Assert.True((await ReadAsync<UploadChunkReceipt>(repeat)).Reused);
        }
        var resumed = await host.GetUploadAsync(tokens.AccessToken, upload.Id);
        Assert.Equal(new[] { 0, 2 }, resumed.ConfirmedChunks);
        using (var incomplete = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", tokens.AccessToken))
            await AuthenticationTestHost.AssertProblemAsync(incomplete, HttpStatusCode.Conflict, "chunks_incomplete");
        using (var middle = await host.PutAsync(tokens.AccessToken, upload.Id, 1, bytes[16..32]))
            Assert.Equal(HttpStatusCode.OK, middle.StatusCode);

        Guid operationId;
        using (var accepted = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var pending = await ReadAsync<UploadSnapshot>(accepted);
            Assert.Equal(UploadState.Finalizing, pending.State);
            Assert.NotNull(pending.Operation);
            operationId = pending.Operation.Id;
        }
        using (var repeated = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
            Assert.Equal(operationId, (await ReadAsync<UploadSnapshot>(repeated)).Operation?.Id);
        }
        await host.ProcessOneAsync();
        var completed = await host.GetUploadAsync(tokens.AccessToken, upload.Id);
        Assert.Equal(UploadState.Completed, completed.State);
        Assert.NotNull(completed.Result);
        var asset = completed.Result;
        using (var repeated = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal(asset.Id, (await ReadAsync<UploadSnapshot>(repeated)).Result?.Id);
        }
        using (var detail = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}", tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var text = await detail.Content.ReadAsStringAsync();
            Assert.DoesNotContain("storageKey", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sha256", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(host.Files.RootPath, text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(name, (await ReadAsync<AssetSnapshot>(detail)).OriginalName);
        }

        EntityTagHeaderValue etag;
        using (var original = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.Equal(bytes, await original.Content.ReadAsByteArrayAsync());
            Assert.Equal("application/octet-stream", original.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", original.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Contains(name, original.Content.Headers.ContentDisposition?.ToString());
            Assert.Equal("nosniff", Assert.Single(original.Headers.GetValues("X-Content-Type-Options")));
            Assert.NotNull(original.Headers.ETag);
            etag = original.Headers.ETag;
            Assert.Equal(34, etag.Tag.Length);
            Assert.NotNull(original.Content.Headers.LastModified);
        }
        using (var range = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", tokens.AccessToken,
            configure: request => request.Headers.Range = new RangeHeaderValue(4, 12)))
        {
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal(bytes[4..13], await range.Content.ReadAsByteArrayAsync());
            Assert.Equal("bytes 4-12/37", range.Content.Headers.ContentRange?.ToString());
        }
        using (var head = await host.SendAsync(HttpMethod.Head, $"/api/assets/{asset.Id}/content", tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(bytes.LongLength, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }
        using (var cached = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", tokens.AccessToken,
            configure: request => request.Headers.IfNoneMatch.Add(etag)))
        {
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.Empty(await cached.Content.ReadAsByteArrayAsync());
        }
        using (var beyond = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", tokens.AccessToken,
            configure: request => request.Headers.Range = new RangeHeaderValue(bytes.Length + 10, null)))
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);
        Assert.DoesNotContain(name, host.Factory.CapturedLogs.Text, StringComparison.Ordinal);
        Assert.False(host.Factory.CapturedLogs.Text.Contains(tokens.AccessToken, StringComparison.Ordinal), "Logs não devem conter tokens.");
    }

    [PostgresFact]
    public async Task UploadsAndOriginalsAreScopedToTheAuthenticatedOwnerEvenWhenContentIsShared()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var owner = await host.LoginAsync();
        var second = await host.CreateSecondOwnerAsync();
        var bytes = new byte[] { 5, 4, 3, 2, 1 };
        var upload = await host.CreateUploadAsync(owner.AccessToken, bytes);
        var asset = await host.ImportAsync(host.OwnerId, bytes, "first-owner.bin");
        var otherAsset = await host.ImportAsync(second.Id, bytes, "second-owner.bin");
        Assert.NotEqual(asset.Id, otherAsset.Id);
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, $"/api/uploads/{upload.Id}"),
            (HttpMethod.Post, $"/api/uploads/{upload.Id}/complete"),
            (HttpMethod.Delete, $"/api/uploads/{upload.Id}"),
            (HttpMethod.Get, $"/api/assets/{asset.Id}"),
            (HttpMethod.Get, $"/api/assets/{asset.Id}/content"),
            (HttpMethod.Head, $"/api/assets/{asset.Id}/content")
        })
        {
            using var response = await host.SendAsync(method, path, second.Tokens.AccessToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using (var response = await host.PutAsync(second.Tokens.AccessToken, upload.Id, 0, bytes))
            await AuthenticationTestHost.AssertProblemAsync(response, HttpStatusCode.NotFound, "upload_not_found");
        using (var firstList = await host.SendAsync(HttpMethod.Get, "/api/assets", owner.AccessToken))
            Assert.Equal(asset.Id, Assert.Single((await ReadAsync<AssetPage>(firstList)).Items).Id);
        using (var secondList = await host.SendAsync(HttpMethod.Get, "/api/assets", second.Tokens.AccessToken))
            Assert.Equal(otherAsset.Id, Assert.Single((await ReadAsync<AssetPage>(secondList)).Items).Id);
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.Blobs.CountAsync());
        Assert.Equal(2, await db.Assets.CountAsync());
    }

    [PostgresFact]
    public async Task ConfirmedChunksAndQueuedCompletionSurviveTwoApiRestarts()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var owner = await host.LoginAsync();
        var bytes = Enumerable.Range(0, 23).Select(value => (byte)value).ToArray();
        var upload = await host.CreateUploadAsync(owner.AccessToken, bytes);
        using (var chunk = await host.PutAsync(owner.AccessToken, upload.Id, 0, bytes[..16]))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        await host.RestartAsync();
        owner = await host.LoginAsync();
        var resumed = await host.GetUploadAsync(owner.AccessToken, upload.Id);
        Assert.Equal(UploadState.Open, resumed.State);
        Assert.Equal([0], resumed.ConfirmedChunks);
        using (var chunk = await host.PutAsync(owner.AccessToken, upload.Id, 1, bytes[16..]))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        Guid? operation;
        using (var complete = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", owner.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, complete.StatusCode);
            operation = (await ReadAsync<UploadSnapshot>(complete)).Operation?.Id;
        }
        await host.RestartAsync();
        owner = await host.LoginAsync();
        var queued = await host.GetUploadAsync(owner.AccessToken, upload.Id);
        Assert.Equal(UploadState.Finalizing, queued.State);
        Assert.Equal(operation, queued.Operation?.Id);
        await host.ProcessOneAsync();
        var completed = await host.GetUploadAsync(owner.AccessToken, upload.Id);
        Assert.Equal(UploadState.Completed, completed.State);
        Assert.NotNull(completed.Result);
        using var content = await host.SendAsync(HttpMethod.Get, $"/api/assets/{completed.Result.Id}/content", owner.AccessToken);
        Assert.Equal(bytes, await content.Content.ReadAsByteArrayAsync());
        await using var scope = host.Factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().BackgroundJobs.CountAsync());
    }

    [PostgresFact]
    public async Task CursorPaginationUsesTheIdTieBreakerAndHidesTrashAndUnavailableBlobs()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var owner = await host.LoginAsync();
        var second = await host.CreateSecondOwnerAsync();
        var assets = new List<AssetSnapshot>();
        for (byte index = 1; index <= 5; index++)
            assets.Add(await host.ImportAsync(host.OwnerId, [index], $"page-{index}.bin"));
        await host.ImportAsync(second.Id, [99], "other-owner.bin");
        var timestamp = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var rows = await db.Assets.Where(asset => asset.OwnerId == host.OwnerId).Include(asset => asset.Blob).ToListAsync();
            foreach (var row in rows) db.Entry(row).Property(asset => asset.UploadedAt).CurrentValue = timestamp;
            rows.Single(row => row.Id == assets[3].Id).MoveToTrash(timestamp.AddMinutes(1));
            rows.Single(row => row.Id == assets[4].Id).Blob.MarkDeleting();
            await db.SaveChangesAsync();
        }
        var listed = new List<Guid>();
        string? cursor = null;
        string? firstCursor = null;
        do
        {
            using var response = await host.SendAsync(HttpMethod.Get,
                "/api/assets?limit=1" + (cursor is null ? "" : "&cursor=" + cursor), owner.AccessToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await ReadAsync<AssetPage>(response);
            var item = Assert.Single(page.Items);
            Assert.DoesNotContain(item.Id, listed);
            listed.Add(item.Id);
            cursor = page.NextCursor;
            firstCursor ??= cursor;
        } while (cursor is not null);
        Assert.Equal(assets.Take(3).Select(asset => asset.Id).OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal), listed);
        foreach (var query in new[] { "limit=0", "limit=101", "cursor=invalid", "cursor=" + firstCursor + "=" })
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/assets?" + query, owner.AccessToken);
            await AuthenticationTestHost.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        }
        using (var foreignCursor = await host.SendAsync(HttpMethod.Get, "/api/assets?cursor=" + firstCursor, second.Tokens.AccessToken))
            await AuthenticationTestHost.AssertProblemAsync(foreignCursor, HttpStatusCode.BadRequest, "invalid_cursor");
        foreach (var unavailable in assets.Skip(3))
        {
            using var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{unavailable.Id}/content", owner.AccessToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [PostgresFact]
    public async Task MissingPhysicalOriginalReturnsSanitizedUnavailableWhileMetadataRemainsReadable()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var owner = await host.LoginAsync();
        var asset = await host.ImportAsync(host.OwnerId, [1, 3, 5, 7], "missing-original.bin");
        string hash;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var blob = await db.Assets.Where(row => row.Id == asset.Id).Select(row => row.Blob).SingleAsync();
            hash = blob.Sha256;
            await scope.ServiceProvider.GetRequiredService<IBlobStorage>().DeleteAsync(blob.StorageKey, CancellationToken.None);
        }
        using (var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", owner.AccessToken))
        {
            await AuthenticationTestHost.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "service_unavailable");
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(host.Files.RootPath, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(hash, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exception", text, StringComparison.OrdinalIgnoreCase);
        }
        using var metadata = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}", owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
    }

    [PostgresFact]
    public async Task ChunkErrorsDoNotConfirmDataAndCancellationIsRepeatable()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var owner = await host.LoginAsync();
        var bytes = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var upload = await host.CreateUploadAsync(owner.AccessToken, bytes);
        using (var type = await host.PutAsync(owner.AccessToken, upload.Id, 0, bytes, contentType: "text/plain"))
            await AuthenticationTestHost.AssertProblemAsync(type, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        using (var header = await host.PutAsync(owner.AccessToken, upload.Id, 0, bytes, "bad-hash"))
            await AuthenticationTestHost.AssertProblemAsync(header, HttpStatusCode.BadRequest, "invalid_request");
        using (var hash = await host.PutAsync(owner.AccessToken, upload.Id, 0, bytes, new string('0', 64)))
            await AuthenticationTestHost.AssertProblemAsync(hash, HttpStatusCode.BadRequest, "chunk_hash_mismatch");
        using (var shortChunk = await host.PutAsync(owner.AccessToken, upload.Id, 0, bytes[..15]))
            await AuthenticationTestHost.AssertProblemAsync(shortChunk, HttpStatusCode.BadRequest, "chunk_size_mismatch");
        using (var large = await host.PutAsync(owner.AccessToken, upload.Id, 0, new byte[17]))
            await AuthenticationTestHost.AssertProblemAsync(large, HttpStatusCode.RequestEntityTooLarge, "chunk_too_large");
        using (var body = new StreamContent(new StorageTestReadStream(new byte[17])))
        {
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var streamed = await host.SendAsync(HttpMethod.Put, $"/api/uploads/{upload.Id}/chunks/0", owner.AccessToken, body);
            await AuthenticationTestHost.AssertProblemAsync(streamed, HttpStatusCode.RequestEntityTooLarge, "chunk_too_large");
        }
        Assert.Empty((await host.GetUploadAsync(owner.AccessToken, upload.Id)).ConfirmedChunks);
        Assert.Empty(host.Files.Files);
        using (var valid = await host.PutAsync(owner.AccessToken, upload.Id, 0, bytes))
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using (var conflict = await host.PutAsync(owner.AccessToken, upload.Id, 0, new byte[16]))
            await AuthenticationTestHost.AssertProblemAsync(conflict, HttpStatusCode.Conflict, "chunk_conflict");
        for (var index = 0; index < 2; index++)
        {
            using var cancel = await host.SendAsync(HttpMethod.Delete, $"/api/uploads/{upload.Id}", owner.AccessToken);
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }
        Assert.Equal(UploadState.Cancelled, (await host.GetUploadAsync(owner.AccessToken, upload.Id)).State);
        using var complete = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", owner.AccessToken);
        await AuthenticationTestHost.AssertProblemAsync(complete, HttpStatusCode.Conflict, "upload_not_open");
    }

    [PostgresFact]
    public async Task StorageStatusSeparatesOwnerLogicalBytesFromSharedPhysicalBytesAndUnreleasedReservations()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        var owner = await host.LoginAsync();
        var second = await host.CreateSecondOwnerAsync();
        var shared = new byte[10];
        await host.ImportAsync(host.OwnerId, shared, "active.bin");
        await host.ImportAsync(second.Id, shared, "same-physical.bin");
        var trashed = await host.ImportAsync(host.OwnerId, [1, 2, 3, 4, 5, 6, 7], "trash.bin");
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var asset = await db.Assets.SingleAsync(row => row.Id == trashed.Id);
            asset.MoveToTrash(asset.UploadedAt.AddMinutes(1));
            await db.SaveChangesAsync();
        }
        var bytes = new byte[20];
        var upload = await host.CreateUploadAsync(owner.AccessToken, bytes);
        using (var chunk = await host.PutAsync(owner.AccessToken, upload.Id, 0, bytes[..16]))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        using (var response = await host.SendAsync(HttpMethod.Get, "/api/storage", owner.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var storage = await ReadAsync<StorageStatusSnapshot>(response);
            Assert.Equal(10, storage.ActiveLibraryBytes);
            Assert.Equal(7, storage.TrashBytes);
            Assert.Equal(17, storage.BlobBytes);
            Assert.Equal(16, storage.TemporaryBytes);
            Assert.Equal(40, storage.ReservedBytes);
            Assert.True(storage.TotalBytes > 0 && storage.AvailableBytes > 0);
        }
        using (var cancel = await host.SendAsync(HttpMethod.Delete, $"/api/uploads/{upload.Id}", owner.AccessToken))
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        using (var response = await host.SendAsync(HttpMethod.Get, "/api/storage", second.Tokens.AccessToken))
        {
            var storage = await ReadAsync<StorageStatusSnapshot>(response);
            Assert.Equal(10, storage.ActiveLibraryBytes);
            Assert.Equal(0, storage.TrashBytes);
            Assert.Equal(40, storage.ReservedBytes);
        }
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IUploadWorkStore>();
            var cleanup = Assert.Single(await store.ListCleanupAsync(10, CancellationToken.None));
            foreach (var key in cleanup.Keys)
                await scope.ServiceProvider.GetRequiredService<ITemporaryStorage>().DeleteAsync(key, CancellationToken.None);
            Assert.True(await store.FinishCleanupAsync(cleanup.UploadId, CancellationToken.None));
        }
        using var cleared = await host.SendAsync(HttpMethod.Get, "/api/storage", owner.AccessToken);
        var final = await ReadAsync<StorageStatusSnapshot>(cleared);
        Assert.Equal(0, final.ReservedBytes);
        Assert.Equal(0, final.TemporaryBytes);
    }

    [PostgresFact]
    public async Task FileRoutesRequireAuthenticationAndInvalidCreatePayloadDoesNotCreateASession()
    {
        await using var host = await FileApiTestHost.CreateAsync();
        foreach (var path in new[] { "/api/assets", "/api/storage", "/api/uploads/" + Guid.NewGuid() })
        {
            using var anonymous = await host.Client.GetAsync(path);
            await AuthenticationTestHost.AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized);
        }
        var owner = await host.LoginAsync();
        foreach (var request in new[]
        {
            new CreateUploadRequest("../escape.bin", 10),
            new CreateUploadRequest("negative.bin", -1),
            new CreateUploadRequest("hash.bin", 10, "bad-hash")
        })
        {
            using var invalid = await host.SendAsync(HttpMethod.Post, "/api/uploads", owner.AccessToken, JsonContent.Create(request));
            await AuthenticationTestHost.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "invalid_request");
        }
        using (var oversized = await host.SendAsync(HttpMethod.Post, "/api/uploads", owner.AccessToken,
            JsonContent.Create(new CreateUploadRequest("too-big.bin", 1025))))
            await AuthenticationTestHost.AssertProblemAsync(oversized, HttpStatusCode.RequestEntityTooLarge, "file_too_large");
        await using var scope = host.Factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().UploadSessions.CountAsync());
        Assert.Empty(host.Files.Files);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
