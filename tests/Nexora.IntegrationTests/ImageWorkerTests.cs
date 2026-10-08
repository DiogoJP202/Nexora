using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Application.Content;
using Nexora.Application.Images;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Application.Uploads;
using Nexora.Domain.Images;
using Nexora.Domain.Jobs;
using Nexora.Infrastructure;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Images;
using Nexora.Infrastructure.Persistence;
using Nexora.Worker.Images;
using SkiaSharp;

namespace Nexora.IntegrationTests;

public sealed class ImageWorkerTests
{
    [PostgresFact]
    public async Task Capture_dates_with_and_without_original_offset_survive_isolated_rendering_postgres_and_http()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var expectedLocal = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Unspecified).AddTicks(1_234_560);
        foreach (var offset in new string?[] { null, "-03:00" })
        {
            var original = ImageCodecTests.AddExif(ImageCodecTests.Encode(32, 16, SKEncodedImageFormat.Jpeg),
                ImageCodecTests.BuildTiff(6, "2024:02:03 04:05:06", offset, "123456"));
            var asset = await host.ImportAsync(original, "private-capture.jpg");
            var work = await host.ClaimAsync();
            await host.ProcessAsync(work);
            DateTimeOffset? expectedUtc = offset is null ? null
                : new DateTimeOffset(expectedLocal, TimeSpan.FromHours(-3)).ToUniversalTime();
            await using (var scope = host.Factory.Services.CreateAsyncScope())
            {
                var image = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Set<BlobImage>()
                    .AsNoTracking().SingleAsync(item => item.BlobId == work.Blob.Id);
                Assert.Equal(expectedLocal, image.CapturedAtLocal);
                Assert.Equal(DateTimeKind.Unspecified, image.CapturedAtLocal!.Value.Kind);
                Assert.Equal(expectedUtc, image.CapturedAtUtc);
                Assert.Equal(16, image.Width);
                Assert.Equal(32, image.Height);
            }
            var metadata = await host.GetAssetAsync(asset.Id, token);
            Assert.NotNull(metadata.Image);
            Assert.Equal(ImageProcessingState.Ready, metadata.Image.State);
            Assert.Equal(expectedLocal, metadata.Image.CapturedAtLocal);
            Assert.Equal(DateTimeKind.Unspecified, metadata.Image.CapturedAtLocal!.Value.Kind);
            Assert.Equal(expectedUtc, metadata.Image.CapturedAtUtc);
            Assert.Equal(16, metadata.Image.Width);
            Assert.Equal(32, metadata.Image.Height);
            using var originalResponse = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", token);
            Assert.Equal(original, await originalResponse.Content.ReadAsByteArrayAsync());
        }
    }

    [PostgresFact]
    public async Task Real_image_worker_renews_a_lease_in_a_separate_scope_while_rendering_is_delayed()
    {
        await using var host = await ImageTestHost.CreateAsync(additionalSettings:
            new Dictionary<string, string?>
            {
                ["Images:LeaseDuration"] = "00:00:06",
                ["Images:LeaseRenewInterval"] = "00:00:02",
                ["Images:ProcessingTimeout"] = "00:00:20",
                ["Images:PollInterval"] = "00:00:00.100",
                ["Images:MaintenanceInterval"] = "00:00:01"
            });
        var asset = await host.ImportAsync(ImageTestHost.Encode(32, 16));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection().AddLogging().AddInfrastructure(
            host.Factory.Services.GetRequiredService<IConfiguration>());
        services.RemoveAll<IImageRenderer>();
        services.AddScoped<IImageRenderer>(provider => new SlowImageRenderer(
            new ProcessImageRenderer(provider.GetRequiredService<IOptions<ImageOptions>>()), started, rendered));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            { ValidateOnBuild = true, ValidateScopes = true });
        using var worker = new ImageWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<ImageOptions>>(), TimeProvider.System, NullLogger<ImageWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            async Task<DateTimeOffset?> ReadExpiryAsync()
            {
                await using var scope = host.Factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().BackgroundJobs.AsNoTracking()
                    .Where(job => job.Kind == "ProcessImage").Select(job => job.LeaseExpiresAt).SingleAsync();
            }
            var initial = await ReadExpiryAsync();
            Assert.NotNull(initial);
            await Task.Delay(TimeSpan.FromMilliseconds(2700));
            var renewed = await ReadExpiryAsync();
            Assert.True(renewed > initial, "The image coordinator must renew its database lease during rendering.");
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (true)
            {
                await using var scope = host.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
                var image = await db.Set<BlobImage>().AsNoTracking().SingleAsync();
                if (image.State == ImageProcessingState.Ready)
                {
                    Assert.Equal(BackgroundJobState.Succeeded, (await db.BackgroundJobs.AsNoTracking()
                        .SingleAsync(job => job.Kind == "ProcessImage")).State);
                    break;
                }
                Assert.True(DateTimeOffset.UtcNow < deadline, "The image worker must commit its rendered result.");
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            var token = await host.LoginAsync();
            Assert.Equal(ImageProcessingState.Ready, (await host.GetAssetAsync(asset.Id, token)).Image?.State);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(stop.Token);
        }
    }

    [PostgresFact]
    public async Task Isolated_renderer_watchdog_stops_the_child_and_terminal_cleanup_preserves_the_original()
    {
        await using var host = await ImageTestHost.CreateAsync(additionalSettings:
            new Dictionary<string, string?>
            {
                ["Images:ProcessingTimeout"] = "00:00:00.001",
                ["Images:MaximumJobAttempts"] = "1"
            });
        var token = await host.LoginAsync();
        var original = ImageTestHost.Encode(32, 16);
        var asset = await host.ImportAsync(original);
        var work = await host.ClaimAsync();
        var stopwatch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<ImageProcessingException>(() => host.ProcessAsync(work));
        stopwatch.Stop();
        Assert.Equal("image_processing_timeout", failure.Code);
        Assert.True(failure.Retryable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), "The decoder child must exit within the watchdog cleanup limit.");
        Assert.DoesNotContain(host.Files.Files, path => path.EndsWith(".png", StringComparison.Ordinal));
        await FailAsync(host, work, failure.Code, failure.Retryable);
        // A configured one-attempt budget makes this retryable failure terminal.
        Assert.Equal(ImageProcessingState.Failed, (await host.GetAssetAsync(asset.Id, token)).Image?.State);
        Assert.True(await host.ReservedBytesAsync() > 0);
        await host.CleanupAsync();
        Assert.Equal(0, await host.ReservedBytesAsync());
        using var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original, await response.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task Image_migration_can_roll_back_with_completed_image_jobs_and_preserves_upload_jobs_for_upgrade()
    {
        const string phase4 = "20261008112515_UploadsDurableJobs";
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var asset = await host.ImportAsync(ImageTestHost.Encode(32, 16));
        await host.ProcessAsync(await host.ClaimAsync());
        Guid uploadId;
        byte[] bytes = [1, 2, 3, 4];
        using (var created = await host.SendContentAsync(HttpMethod.Post, "/api/uploads", token,
            JsonContent.Create(new CreateUploadRequest("migration-retained.bin", bytes.Length))))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var upload = await created.Content.ReadFromJsonAsync<UploadSnapshot>();
            Assert.NotNull(upload);
            uploadId = upload.Id;
        }
        var binary = new ByteArrayContent(bytes);
        binary.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using (var chunk = await host.SendContentAsync(HttpMethod.Put, $"/api/uploads/{uploadId}/chunks/0", token, binary))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        using (var queued = await host.SendAsync(HttpMethod.Post, $"/api/uploads/{uploadId}/complete", token))
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Guid uploadJobId;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            uploadJobId = await db.BackgroundJobs.Where(job => job.Kind == "FinalizeUpload")
                .Select(job => job.Id).SingleAsync();
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(job => job.Kind == "ProcessImage"));
            await db.GetService<IMigrator>().MigrateAsync(phase4);
            db.ChangeTracker.Clear();
            Assert.Equal(phase4, (await db.Database.GetAppliedMigrationsAsync()).Last());
            // Select only phase-4 columns while the new BlobId column is absent.
            var retained = await db.BackgroundJobs.Where(job => job.Kind == "FinalizeUpload")
                .Select(job => new { job.Id, job.UploadSessionId }).SingleAsync();
            Assert.Equal(uploadJobId, retained.Id);
            Assert.Equal(uploadId, retained.UploadSessionId);
            Assert.Equal(0, await db.BackgroundJobs.CountAsync(job => job.Kind == "ProcessImage"));
            Assert.Equal(asset.Id, await db.Assets.Select(row => row.Id).SingleAsync());
            await db.GetService<IMigrator>().MigrateAsync();
            db.ChangeTracker.Clear();
            await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().BackfillAsync(100, CancellationToken.None);
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(job => job.Kind == "ProcessImage"));
            Assert.Equal(uploadJobId, await db.BackgroundJobs.Where(job => job.Kind == "FinalizeUpload")
                .Select(job => job.Id).SingleAsync());
            Assert.Equal(ImageProcessingState.Pending, (await db.Set<BlobImage>().AsNoTracking().SingleAsync()).State);
        }
        Assert.Equal(ImageProcessingState.Pending, (await host.GetAssetAsync(asset.Id, token)).Image?.State);
    }

    [PostgresFact]
    public async Task Capacity_wait_does_not_consume_attempts_or_prevent_original_download()
    {
        await using var host = await ImageTestHost.CreateAsync(additionalSettings:
            new Dictionary<string, string?> { ["Uploads:MaximumReservedBytes"] = "1048576" });
        var token = await host.LoginAsync();
        var original = ImageTestHost.Encode(32, 16);
        var asset = await host.ImportAsync(original);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            Assert.Null(await services.GetRequiredService<IImageWorkStore>().ClaimAsync(CancellationToken.None));
            var job = await services.GetRequiredService<NexoraDbContext>().BackgroundJobs.AsNoTracking()
                .SingleAsync(item => item.Kind == "ProcessImage");
            Assert.Equal(0, job.Attempts);
            Assert.Equal(BackgroundJobState.Pending, job.State);
            Assert.Equal("insufficient_storage", job.FailureCode);
        }
        var pending = await host.GetAssetAsync(asset.Id, token);
        Assert.Equal(ImageProcessingState.Pending, pending.Image?.State);
        Assert.Equal(0, await host.ReservedBytesAsync());
        using var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original, await response.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task Concurrent_backfill_enqueues_one_job_for_an_existing_ready_image_without_changing_the_asset()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var original = ImageTestHost.Encode(32, 16);
        var asset = await host.ImportAsync(original);
        // Remove only the new phase-5 records to represent a phase-4 database.
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            await db.BackgroundJobs.Where(job => job.Kind == "ProcessImage").ExecuteDeleteAsync();
            await db.Set<BlobImage>().ExecuteDeleteAsync();
        }
        async Task BackfillAsync()
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().BackfillAsync(100, CancellationToken.None);
        }
        await Task.WhenAll(BackfillAsync(), BackfillAsync());
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(job => job.Kind == "ProcessImage"));
            Assert.Equal(1, await db.Set<BlobImage>().CountAsync());
            Assert.Equal(asset.Id, (await db.Assets.AsNoTracking().SingleAsync()).Id);
        }
        await host.ProcessAsync(await host.ClaimAsync());
        var token = await host.LoginAsync();
        Assert.Equal(ImageProcessingState.Ready, (await host.GetAssetAsync(asset.Id, token)).Image?.State);
    }

    [PostgresFact]
    public async Task Malformed_image_fails_visibly_while_original_remains_downloadable_and_cleanup_releases_reservation()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        byte[] invalid = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];
        var asset = await host.ImportAsync(invalid, "malformed.png");
        var work = await host.ClaimAsync();
        Assert.True(await host.ReservedBytesAsync() > 0);
        var failure = await Assert.ThrowsAsync<ImageProcessingException>(() => host.ProcessAsync(work));
        Assert.Equal("image_invalid", failure.Code);
        Assert.False(failure.Retryable);
        await FailAsync(host, work, failure.Code, failure.Retryable);
        var failed = await host.GetAssetAsync(asset.Id, token);
        Assert.Equal(ImageProcessingState.Failed, failed.Image?.State);
        Assert.Equal("image_invalid", failed.Image?.FailureCode);
        Assert.False(failed.Image!.HasThumbnail);
        using (var thumbnail = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/thumbnail", token))
            Assert.Equal(HttpStatusCode.NotFound, thumbnail.StatusCode);
        using (var original = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", token))
        {
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.Equal("attachment", original.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(invalid, await original.Content.ReadAsByteArrayAsync());
        }
        await host.CleanupAsync();
        Assert.Equal(0, await host.ReservedBytesAsync());
        await using var scope = host.Factory.Services.CreateAsyncScope();
        Assert.Equal(BackgroundJobState.Failed, (await scope.ServiceProvider.GetRequiredService<NexoraDbContext>()
            .BackgroundJobs.AsNoTracking().SingleAsync(job => job.Kind == "ProcessImage")).State);
    }

    [PostgresFact]
    public async Task Pixel_and_input_limits_fail_before_derivative_publication_and_preserve_originals()
    {
        var original = ImageTestHost.Encode(64, 64);
        foreach (var (option, value, code) in new[]
        {
            ("Images:MaximumPixels", "1000", "image_pixel_limit_exceeded"),
            ("Images:MaximumInputBytes", "64", "image_input_too_large")
        })
        {
            await using var host = await ImageTestHost.CreateAsync(additionalSettings:
                new Dictionary<string, string?> { [option] = value });
            var token = await host.LoginAsync();
            var asset = await host.ImportAsync(original);
            var work = await host.ClaimAsync();
            var failure = await Assert.ThrowsAsync<ImageProcessingException>(() => host.ProcessAsync(work));
            Assert.Equal(code, failure.Code);
            await FailAsync(host, work, failure.Code, failure.Retryable);
            Assert.DoesNotContain(host.Files.Files, path => path.EndsWith(".png", StringComparison.Ordinal));
            using var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", token);
            Assert.Equal(original, await response.Content.ReadAsByteArrayAsync());
            await host.CleanupAsync();
            Assert.Equal(0, await host.ReservedBytesAsync());
        }
    }

    [PostgresFact]
    public async Task Partial_derivative_publication_is_retried_with_a_new_generation_and_old_attempt_is_removed()
    {
        var clock = new MutableTimeProvider();
        await using var host = await ImageTestHost.CreateAsync(clock);
        var asset = await host.ImportAsync(ImageTestHost.Encode(64, 32));
        var first = await host.ClaimAsync();
        var abandoned = new DerivativeKey(first.Lease.Token, DerivativeKind.Thumbnail);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var processor = new ImageJobProcessor(services.GetRequiredService<IImageWorkStore>(),
                services.GetRequiredService<IBlobStorage>(), services.GetRequiredService<IImageRenderer>(),
                new PublicationFailureStorage(services.GetRequiredService<IDerivativeStorage>()));
            await Assert.ThrowsAsync<IOException>(() => processor.ProcessAsync(first, CancellationToken.None));
            Assert.NotNull(await services.GetRequiredService<IDerivativeStorage>().GetInfoAsync(abandoned, CancellationToken.None));
        }
        await FailAsync(host, first, "storage_unavailable", true);
        Assert.True(await host.ReservedBytesAsync() > 0);
        clock.Advance(TimeSpan.FromSeconds(31));
        var retry = await host.ClaimAsync();
        Assert.NotEqual(first.Lease.Token, retry.Lease.Token);
        Assert.Contains(first.Lease.Token, retry.PreviousAttempts);
        await host.ProcessAsync(retry);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            Assert.Null(await services.GetRequiredService<IDerivativeStorage>().GetInfoAsync(abandoned, CancellationToken.None));
            var image = await services.GetRequiredService<NexoraDbContext>().Set<BlobImage>().AsNoTracking().SingleAsync();
            Assert.Equal(retry.Lease.Token, image.DerivativeGenerationId);
            Assert.Equal(ImageProcessingState.Ready, image.State);
        }
        await host.CleanupAsync();
        Assert.Equal(0, await host.ReservedBytesAsync());
        var token = await host.LoginAsync();
        using var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/thumbnail", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresFact]
    public async Task Lost_completion_acknowledgment_preserves_the_committed_generation_until_cleanup()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var asset = await host.ImportAsync(ImageTestHost.Encode(64, 32));
        var work = await host.ClaimAsync();
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var processor = new ImageJobProcessor(new AmbiguousCompletionStore(services.GetRequiredService<IImageWorkStore>()),
                services.GetRequiredService<IBlobStorage>(), services.GetRequiredService<IImageRenderer>(),
                services.GetRequiredService<IDerivativeStorage>());
            await Assert.ThrowsAsync<IOException>(() => processor.ProcessAsync(work, CancellationToken.None));
            Assert.False(await services.GetRequiredService<IImageWorkStore>()
                .FailAsync(work.Lease, "storage_unavailable", true, CancellationToken.None));
            var image = await services.GetRequiredService<NexoraDbContext>().Set<BlobImage>().AsNoTracking().SingleAsync();
            Assert.Equal(ImageProcessingState.Ready, image.State);
            Assert.Equal(work.Lease.Token, image.DerivativeGenerationId);
        }
        await host.CleanupAsync();
        Assert.Equal(0, await host.ReservedBytesAsync());
        var token = await host.LoginAsync();
        foreach (var kind in new[] { "thumbnail", "preview" })
        {
            using var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/{kind}", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ImageTestHost.AssertDimensions(await response.Content.ReadAsByteArrayAsync(), 64, 32);
        }
    }

    [PostgresFact]
    public async Task Expired_lease_cannot_change_the_winner_and_cleanup_removes_late_files_from_the_old_generation()
    {
        var clock = new MutableTimeProvider();
        await using var host = await ImageTestHost.CreateAsync(clock);
        await host.ImportAsync(ImageTestHost.Encode(64, 32));
        var first = await host.ClaimAsync();
        var staleBytes = ImageTestHost.Encode(16, 8);
        var staleKey = new DerivativeKey(first.Lease.Token, DerivativeKind.Thumbnail);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IDerivativeStorage>().PublishAsync(staleKey, staleBytes, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));
        var winner = await host.ClaimAsync();
        await host.ProcessAsync(winner);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var storage = services.GetRequiredService<IDerivativeStorage>();
            // Simulate a delayed old process writing after its replacement has completed.
            var staleInfo = await storage.PublishAsync(staleKey, staleBytes, CancellationToken.None);
            var store = services.GetRequiredService<IImageWorkStore>();
            Assert.False(await store.RenewAsync(first.Lease, CancellationToken.None));
            Assert.False(await store.CompleteAsync(first.Lease, new RenderedImage(16, 8, null, null, staleBytes, staleBytes),
                staleInfo, staleInfo, CancellationToken.None));
            Assert.False(await store.FailAsync(first.Lease, "storage_unavailable", true, CancellationToken.None));
            Assert.Equal(winner.Lease.Token, (await services.GetRequiredService<NexoraDbContext>()
                .Set<BlobImage>().AsNoTracking().SingleAsync()).DerivativeGenerationId);
        }
        await host.CleanupAsync();
        Assert.Equal(0, await host.ReservedBytesAsync());
        await using var verification = host.Factory.Services.CreateAsyncScope();
        var derivatives = verification.ServiceProvider.GetRequiredService<IDerivativeStorage>();
        Assert.Null(await derivatives.GetInfoAsync(staleKey, CancellationToken.None));
        Assert.NotNull(await derivatives.GetInfoAsync(new DerivativeKey(winner.Lease.Token, DerivativeKind.Thumbnail), CancellationToken.None));
    }

    [PostgresFact]
    public async Task Concurrent_claims_create_one_lease_and_repeated_abandonment_exhausts_the_attempt_budget()
    {
        var clock = new MutableTimeProvider();
        await using var host = await ImageTestHost.CreateAsync(clock);
        await host.ImportAsync(ImageTestHost.Encode(32, 16));
        async Task<ImageWorkItem?> TryClaimAsync()
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IImageWorkStore>().ClaimAsync(CancellationToken.None);
        }
        var leases = await Task.WhenAll(TryClaimAsync(), TryClaimAsync());
        Assert.Single(leases, work => work is not null);
        for (var attempt = 1; attempt < 3; attempt++)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.NotNull(await TryClaimAsync());
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await TryClaimAsync());
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var job = await db.BackgroundJobs.AsNoTracking().SingleAsync(item => item.Kind == "ProcessImage");
            Assert.Equal(3, job.Attempts);
            Assert.Equal(BackgroundJobState.Failed, job.State);
            Assert.Equal(ImageProcessingState.Failed, (await db.Set<BlobImage>().AsNoTracking().SingleAsync()).State);
        }
        await host.CleanupAsync();
        Assert.Equal(0, await host.ReservedBytesAsync());
    }

    [PostgresFact]
    public async Task Changed_original_is_rejected_by_its_hash_and_is_not_used_to_publish_previews()
    {
        await using var host = await ImageTestHost.CreateAsync();
        var token = await host.LoginAsync();
        var original = ImageTestHost.Encode(64, 32);
        var asset = await host.ImportAsync(original);
        var work = await host.ClaimAsync();
        var changed = original.ToArray();
        changed[^1] ^= 1;
        var path = Path.Combine(host.Files.RootPath, work.Blob.StorageKey.ToString().Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(path, changed);
        var failure = await Assert.ThrowsAsync<ImageProcessingException>(() => host.ProcessAsync(work));
        Assert.Equal("image_integrity_failed", failure.Code);
        await FailAsync(host, work, failure.Code, false);
        Assert.DoesNotContain(host.Files.Files, file => file.EndsWith(".png", StringComparison.Ordinal));
        Assert.Equal(ImageProcessingState.Failed, (await host.GetAssetAsync(asset.Id, token)).Image?.State);
        using var response = await host.SendAsync(HttpMethod.Get, $"/api/assets/{asset.Id}/content", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(changed, await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task FailAsync(ImageTestHost host, ImageWorkItem work, string code, bool retryable)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IImageWorkStore>()
            .FailAsync(work.Lease, code, retryable, CancellationToken.None));
    }

    private sealed class SlowImageRenderer(IImageRenderer actual, TaskCompletionSource started,
        TaskCompletionSource rendered) : IImageRenderer
    {
        public async Task<RenderedImage> RenderAsync(BlobDescriptor blob, Stream original, CancellationToken ct)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
                var result = await actual.RenderAsync(blob, original, ct);
                rendered.TrySetResult();
                return result;
            }
            catch (Exception failure)
            {
                rendered.TrySetException(failure);
                throw;
            }
        }
    }

    private sealed class PublicationFailureStorage(IDerivativeStorage actual) : IDerivativeStorage
    {
        public async Task<DerivativeInfo> PublishAsync(DerivativeKey key, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            await actual.PublishAsync(key, content, ct);
            throw new IOException("Simulated interruption after derivative publication.");
        }
        public Task<Stream> OpenReadAsync(DerivativeKey key, CancellationToken ct) => actual.OpenReadAsync(key, ct);
        public Task<DerivativeInfo?> GetInfoAsync(DerivativeKey key, CancellationToken ct) => actual.GetInfoAsync(key, ct);
        public Task CleanAttemptAsync(Guid id, bool preservePublished, CancellationToken ct) => actual.CleanAttemptAsync(id, preservePublished, ct);
    }

    private sealed class AmbiguousCompletionStore(IImageWorkStore actual) : IImageWorkStore
    {
        public async Task<bool> CompleteAsync(ImageJobLease lease, RenderedImage image, DerivativeInfo thumbnail,
            DerivativeInfo preview, CancellationToken ct)
        {
            Assert.True(await actual.CompleteAsync(lease, image, thumbnail, preview, ct));
            throw new IOException("Simulated lost acknowledgment after image completion commit.");
        }
        public Task<ImageWorkItem?> ClaimAsync(CancellationToken ct) => actual.ClaimAsync(ct);
        public Task<bool> RenewAsync(ImageJobLease lease, CancellationToken ct) => actual.RenewAsync(lease, ct);
        public Task<bool> FailAsync(ImageJobLease lease, string code, bool retryable, CancellationToken ct) => actual.FailAsync(lease, code, retryable, ct);
        public Task<ImageCleanupItem[]> ListCleanupAsync(int limit, CancellationToken ct) => actual.ListCleanupAsync(limit, ct);
        public Task<bool> FinishCleanupAsync(Guid jobId, CancellationToken ct) => actual.FinishCleanupAsync(jobId, ct);
        public Task BackfillAsync(int limit, CancellationToken ct) => actual.BackfillAsync(limit, ct);
    }
}
