using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Content;
using Nexora.Application.Images;
using Nexora.Application.Jobs;
using Nexora.Application.Uploads;
using Nexora.Domain.Images;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure.Persistence;
using SkiaSharp;

namespace Nexora.IntegrationTests;

public sealed class ImagesApiTests
{
    [PostgresFact]
    public async Task Completing_a_chunk_upload_queues_image_processing_together_with_the_ready_asset()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var original = ImageTestHost.Encode(32, 16);
        UploadSnapshot upload;
        using (var created = await host.SendContentAsync(HttpMethod.Post, "/api/uploads", token,
            JsonContent.Create(new CreateUploadRequest("uploaded-image.png", original.Length))))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var result = await created.Content.ReadFromJsonAsync<UploadSnapshot>();
            Assert.NotNull(result);
            upload = result;
        }
        Assert.Equal(1, upload.ChunkCount);
        var binary = new ByteArrayContent(original);
        binary.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using (var chunk = await host.SendContentAsync(HttpMethod.Put, $"/api/uploads/{upload.Id}/chunks/0", token, binary))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        using (var queued = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", token))
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var work = await services.GetRequiredService<IUploadWorkStore>().ClaimAsync(CancellationToken.None);
            Assert.NotNull(work);
            await services.GetRequiredService<IUploadJobProcessor>().ProcessAsync(work, CancellationToken.None);
        }
        Guid assetId;
        using (var finished = await host.SendAsync(HttpMethod.Get, $"/api/uploads/{upload.Id}", token))
        {
            Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
            var result = await finished.Content.ReadFromJsonAsync<UploadSnapshot>();
            Assert.NotNull(result);
            Assert.Equal(UploadState.Completed, result.State);
            Assert.NotNull(result.Result);
            assetId = result.Result.Id;
        }
        var pending = await host.GetAssetAsync(assetId, token);
        Assert.Equal(ImageProcessingState.Pending, pending.Image?.State);
        await host.ProcessAsync(await host.ClaimAsync());
        Assert.Equal(ImageProcessingState.Ready, (await host.GetAssetAsync(assetId, token)).Image?.State);
        using var originalResponse = await host.SendAsync(HttpMethod.Get, $"/api/assets/{assetId}/content", token);
        Assert.Equal(original, await originalResponse.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task Supported_originals_produce_bounded_png_derivatives_through_the_isolated_renderer()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        foreach (var format in new[] { SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Webp })
        {
            var original = ImageTestHost.Encode(2000, 1000, format);
            var asset = await host.ImportAsync(original, $"private-{format}.image");
            var pending = await host.GetAssetAsync(asset.Id, token);
            Assert.Equal(ImageProcessingState.Pending, pending.Image?.State);
            using (var unavailable = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/thumbnail", token))
                Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
            await host.ProcessAsync(await host.ClaimAsync());
            var ready = await host.GetAssetAsync(asset.Id, token);
            Assert.NotNull(ready.Image);
            Assert.Equal(ImageProcessingState.Ready, ready.Image.State);
            Assert.Equal(2000, ready.Image.Width);
            Assert.Equal(1000, ready.Image.Height);
            Assert.True(ready.Image.HasThumbnail);
            Assert.True(ready.Image.HasPreview);
            Assert.NotNull(ready.Image.ProcessedAt);
            Assert.Null(ready.Image.CapturedAtLocal);
            Assert.Null(ready.Image.CapturedAtUtc);

            foreach (var (route, width, height) in new[] { ("thumbnail", 256, 128), ("preview", 1280, 640) })
            {
                using var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/{route}", token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
                Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
                Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
                Assert.Contains("default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
                ImageTestHost.AssertDimensions(await response.Content.ReadAsByteArrayAsync(), width, height);
            }
            using var content = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", token);
            Assert.Equal(original, await content.Content.ReadAsByteArrayAsync());
            Assert.Equal("application/octet-stream", content.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", content.Content.Headers.ContentDisposition?.DispositionType);
        }
        await host.CleanupAsync();
        Assert.Equal(0, await host.ReservedBytesAsync());
        Assert.DoesNotContain("private-Png.image", host.Factory.CapturedLogs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(token, host.Factory.CapturedLogs.Text, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Small_images_are_not_upscaled_and_derivatives_support_head_range_and_conditional_requests()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var asset = await host.ImportAsync(ImageTestHost.Encode(32, 16));
        await host.ProcessAsync(await host.ClaimAsync());
        foreach (var route in new[] { "thumbnail", "preview" })
        {
            var path = $"/api/assets/{asset.Id}/{route}";
            byte[] bytes;
            EntityTagHeaderValue etag;
            using (var response = await host.SendAsync(HttpMethod.Get, path, token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                bytes = await response.Content.ReadAsByteArrayAsync();
                ImageTestHost.AssertDimensions(bytes, 32, 16);
                Assert.NotNull(response.Headers.ETag);
                etag = response.Headers.ETag;
                Assert.NotNull(response.Content.Headers.LastModified);
            }
            using (var head = await host.SendAsync(HttpMethod.Head, path, token))
            {
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
                Assert.Equal(bytes.LongLength, head.Content.Headers.ContentLength);
                Assert.Empty(await head.Content.ReadAsByteArrayAsync());
                Assert.Equal(etag, head.Headers.ETag);
            }
            using (var range = await host.SendAsync(HttpMethod.Get, path, token,
                request => request.Headers.Range = new RangeHeaderValue(0, 7)))
            {
                Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
                Assert.Equal(bytes[..8], await range.Content.ReadAsByteArrayAsync());
            }
            using (var conditional = await host.SendAsync(HttpMethod.Get, path, token,
                request => request.Headers.IfNoneMatch.Add(etag)))
            {
                Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
                Assert.Empty(await conditional.Content.ReadAsByteArrayAsync());
            }
            using var outside = await host.SendAsync(HttpMethod.Get, path, token,
                request => request.Headers.Range = new RangeHeaderValue(bytes.Length, null));
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, outside.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Shared_blob_is_processed_once_but_derivative_access_remains_scoped_to_owner_and_trash()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var second = await host.CreateSecondOwnerAsync();
        var original = ImageTestHost.Encode(100, 50);
        var first = await host.ImportAsync(original, "first-owner.png");
        var other = await host.ImportAsync(original, "second-owner.png", second.Id);
        var repeat = await host.ImportAsync(original, "renamed-reupload.png");
        Assert.Equal(first.Id, repeat.Id);
        Assert.Equal("first-owner.png", repeat.OriginalName);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(1, await db.Blobs.CountAsync());
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(job => job.Kind == "ProcessImage"));
        }
        await host.ProcessAsync(await host.ClaimAsync());
        foreach (var route in new[] { "thumbnail", "preview" })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                using var foreign = await host.SendAsync(method, $"/api/assets/{first.Id}/{route}", second.Token);
                Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
                using var anonymous = await host.SendAsync(method, $"/api/assets/{first.Id}/{route}", null);
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }
            using var own = await host.SendAsync(HttpMethod.Get, $"/api/assets/{first.Id}/{route}", token);
            using var shared = await host.SendAsync(HttpMethod.Get, $"/api/assets/{other.Id}/{route}", second.Token);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
            Assert.Equal(await own.Content.ReadAsByteArrayAsync(), await shared.Content.ReadAsByteArrayAsync());
            Assert.Equal(own.Headers.ETag, shared.Headers.ETag);
        }
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var row = await db.Assets.SingleAsync(asset => asset.Id == first.Id);
            row.MoveToTrash(row.UploadedAt.AddMinutes(1));
            await db.SaveChangesAsync();
        }
        foreach (var route in new[] { "thumbnail", "preview" })
        {
            using var trashed = await host.SendAsync(HttpMethod.Get, $"/api/assets/{first.Id}/{route}", token);
            Assert.Equal(HttpStatusCode.NotFound, trashed.StatusCode);
            using var retained = await host.SendAsync(HttpMethod.Get, $"/api/assets/{other.Id}/{route}", second.Token);
            Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Missing_or_tampered_derivative_returns_sanitized_unavailable_without_losing_original()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var original = ImageTestHost.Encode(32, 16);
        var asset = await host.ImportAsync(original);
        await host.ProcessAsync(await host.ClaimAsync());
        Guid generation;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            generation = (await db.Set<BlobImage>().AsNoTracking().SingleAsync()).DerivativeGenerationId!.Value;
        }
        var key = new DerivativeKey(generation, DerivativeKind.Thumbnail);
        var path = Path.Combine(host.Files.RootPath, key.ToString().Replace('/', Path.DirectorySeparatorChar));
        var valid = await File.ReadAllBytesAsync(path);
        var corrupted = valid.ToArray();
        corrupted[^1] ^= 1;
        await File.WriteAllBytesAsync(path, corrupted);
        using (var failed = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/thumbnail", token))
        {
            await AuthenticationTestHost.AssertProblemAsync(failed, HttpStatusCode.ServiceUnavailable, "service_unavailable");
            var text = await failed.Content.ReadAsStringAsync();
            Assert.DoesNotContain(host.Files.RootPath, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(generation.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }
        File.Delete(path);
        using (var missing = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/thumbnail", token))
            await AuthenticationTestHost.AssertProblemAsync(missing, HttpStatusCode.ServiceUnavailable, "service_unavailable");
        var readable = await host.GetAssetAsync(asset.Id, token);
        Assert.Equal(ImageProcessingState.Ready, readable.Image?.State);
        using var content = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", token);
        Assert.Equal(original, await content.Content.ReadAsByteArrayAsync());
        using var preview = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/preview", token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    [PostgresFact]
    public async Task Ordinary_files_have_no_image_state_or_queued_processing()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var asset = await host.ImportAsync([1, 2, 3, 4], "plain.bin");
        Assert.Null((await host.GetAssetAsync(asset.Id, token)).Image);
        await using var scope = host.Factory.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().ClaimAsync(CancellationToken.None));
        foreach (var route in new[] { "thumbnail", "preview" })
        {
            using var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/{route}", token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
