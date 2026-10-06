using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Domain.Content;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

public sealed class ContentIngestionTests
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("content");

    [PostgresFact]
    public async Task SameOwnerReuploadReusesTheAssetAndPreservesItsMetadata()
    {
        await using var host = await ContentTestHost.CreateAsync();
        var ownerId = await host.CreateOwnerAsync();
        var initial = await host.ImportAsync(ownerId, "Original.txt", Bytes);
        Assert.Equal(AssetImportStatus.Created, initial.Status);
        Assert.NotNull(initial.Asset);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            (await context.Assets.SingleAsync()).SetFavorite(true);
            await context.SaveChangesAsync();
        }

        var repeated = await host.ImportAsync(ownerId, "Different filename.txt", Bytes,
            Convert.ToHexString(SHA256.HashData(Bytes)));

        Assert.Equal(AssetImportStatus.Reused, repeated.Status);
        Assert.NotNull(repeated.Asset);
        Assert.Equal(initial.Asset.Id, repeated.Asset.Id);
        Assert.Equal("Original.txt", repeated.Asset.OriginalName);
        Assert.Equal(initial.Asset.UploadedAt.ToUnixTimeMilliseconds(), repeated.Asset.UploadedAt.ToUnixTimeMilliseconds());
        Assert.True(repeated.Asset.IsFavorite);
        await using var inspection = host.Services.CreateAsyncScope();
        var database = inspection.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await database.Assets.CountAsync());
        Assert.Equal(BlobState.Ready, (await database.Blobs.SingleAsync()).State);
        Assert.Single(host.Files.Files);
    }

    [PostgresFact]
    public async Task DifferentOwnersShareThePhysicalBlobButKeepSeparateAssets()
    {
        await using var host = await ContentTestHost.CreateAsync();
        var firstOwner = await host.CreateOwnerAsync();
        var secondOwner = await host.CreateOwnerAsync();
        var first = await host.ImportAsync(firstOwner, "First.txt", Bytes);
        var second = await host.ImportAsync(secondOwner, "Second.txt", Bytes);

        Assert.Equal(AssetImportStatus.Created, first.Status);
        Assert.Equal(AssetImportStatus.Created, second.Status);
        Assert.NotNull(first.Asset);
        Assert.NotNull(second.Asset);
        Assert.True(first.Asset.Id != second.Asset.Id);
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var assets = await context.Assets.AsNoTracking().ToListAsync();
        Assert.Equal(2, assets.Count);
        Assert.Single(assets.Select(asset => asset.BlobId).Distinct());
        Assert.Equal(2, assets.Select(asset => asset.OwnerId).Distinct().Count());
        Assert.Equal(1, await context.Blobs.CountAsync());
        Assert.Single(host.Files.Files);
    }

    [PostgresFact]
    public async Task ReuploadOfAnAssetInTrashRequiresExplicitRestore()
    {
        await using var host = await ContentTestHost.CreateAsync();
        var ownerId = await host.CreateOwnerAsync();
        var initial = await host.ImportAsync(ownerId, "Original.txt", Bytes);
        Assert.NotNull(initial.Asset);
        var deletedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var asset = await context.Assets.SingleAsync();
            asset.SetFavorite(true);
            asset.MoveToTrash(deletedAt);
            await context.SaveChangesAsync();
        }

        var retry = await host.ImportAsync(ownerId, "Replacement.txt", Bytes);

        Assert.Equal(AssetImportStatus.AssetInTrash, retry.Status);
        Assert.NotNull(retry.Asset);
        Assert.Equal(initial.Asset.Id, retry.Asset.Id);
        Assert.Equal("Original.txt", retry.Asset.OriginalName);
        Assert.True(retry.Asset.IsFavorite);
        Assert.Equal(deletedAt.ToUnixTimeMilliseconds(), retry.Asset.DeletedAt?.ToUnixTimeMilliseconds());
        await using var inspection = host.Services.CreateAsyncScope();
        Assert.Equal(1, await inspection.ServiceProvider.GetRequiredService<NexoraDbContext>().Assets.CountAsync());
        Assert.Single(host.Files.Files);
    }

    [PostgresFact]
    public async Task ConcurrentIdenticalImportsCreateOneBlobAndOneAsset()
    {
        await using var host = await ContentTestHost.CreateAsync();
        var ownerId = await host.CreateOwnerAsync();
        await using var firstScope = host.Services.CreateAsyncScope();
        await using var secondScope = host.Services.CreateAsyncScope();
        await using var firstInput = new StorageTestReadStream(Bytes);
        await using var secondInput = new StorageTestReadStream(Bytes);
        var results = await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<IAssetIngestionService>().ImportAsync(
                new AssetImportRequest(ownerId, "First.txt", Bytes.Length), firstInput, CancellationToken.None),
            secondScope.ServiceProvider.GetRequiredService<IAssetIngestionService>().ImportAsync(
                new AssetImportRequest(ownerId, "Second.txt", Bytes.Length), secondInput, CancellationToken.None));

        Assert.Single(results, result => result.Status == AssetImportStatus.Created);
        Assert.Single(results, result => result.Status == AssetImportStatus.Reused);
        Assert.NotNull(results[0].Asset);
        Assert.NotNull(results[1].Asset);
        Assert.True(results[0].Asset?.Id == results[1].Asset?.Id);
        await using var inspection = host.Services.CreateAsyncScope();
        var context = inspection.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await context.Assets.CountAsync());
        Assert.Equal(BlobState.Ready, (await context.Blobs.SingleAsync()).State);
        Assert.Single(host.Files.Files);
    }

    [PostgresFact]
    public async Task InterruptedPublicationLeavesStagingAndRetryRepairsTheSameGeneration()
    {
        await using var host = await ContentTestHost.CreateAsync(failAfterPublication: true);
        var ownerId = await host.CreateOwnerAsync();

        await Assert.ThrowsAsync<IOException>(() => host.ImportAsync(ownerId, "Interrupted.txt", Bytes));
        Guid stagingId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var staging = await context.Blobs.SingleAsync();
            stagingId = staging.Id;
            Assert.Equal(BlobState.Staging, staging.State);
            Assert.Equal(0, await context.Assets.CountAsync());
            Assert.NotNull(await host.Files.Blobs.GetInfoAsync(staging.StorageKey, CancellationToken.None));
        }

        Assert.Single(host.Files.Files);
        var repaired = await host.ImportAsync(ownerId, "Recovered.txt", Bytes);
        Assert.Equal(AssetImportStatus.Created, repaired.Status);
        await using var inspection = host.Services.CreateAsyncScope();
        var database = inspection.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var ready = await database.Blobs.SingleAsync();
        Assert.Equal(stagingId, ready.Id);
        Assert.Equal(BlobState.Ready, ready.State);
        Assert.Equal(stagingId, (await database.Assets.SingleAsync()).BlobId);
        Assert.Single(host.Files.Files);
    }

    [PostgresFact]
    public async Task DeletingBlobCannotBeResurrectedByAReupload()
    {
        await using var host = await ContentTestHost.CreateAsync();
        var firstOwner = await host.CreateOwnerAsync();
        var secondOwner = await host.CreateOwnerAsync();
        _ = await host.ImportAsync(firstOwner, "Original.txt", Bytes);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            (await context.Blobs.SingleAsync()).MarkDeleting();
            await context.SaveChangesAsync();
        }

        var result = await host.ImportAsync(secondOwner, "Other.txt", Bytes);

        Assert.Equal(AssetImportStatus.BlobUnavailable, result.Status);
        Assert.Null(result.Asset);
        await using var inspection = host.Services.CreateAsyncScope();
        var database = inspection.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(BlobState.Deleting, (await database.Blobs.SingleAsync()).State);
        Assert.Equal(1, await database.Assets.CountAsync());
        Assert.Single(host.Files.Files);
    }

    [PostgresFact]
    public async Task ClientHashMismatchNeverCreatesCatalogRowsOrLeavesTemporaryFiles()
    {
        await using var host = await ContentTestHost.CreateAsync();
        var ownerId = await host.CreateOwnerAsync();

        await Assert.ThrowsAsync<StorageIntegrityException>(() => host.ImportAsync(ownerId, "Claimed.txt", Bytes, new string('0', 64)));

        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await context.Blobs.CountAsync());
        Assert.Equal(0, await context.Assets.CountAsync());
        Assert.Empty(host.Files.Files);
    }

    [PostgresFact]
    public async Task ACorruptedReadyBlobIsDetectedInsteadOfReturningAReusableAsset()
    {
        await using var host = await ContentTestHost.CreateAsync();
        var ownerId = await host.CreateOwnerAsync();
        _ = await host.ImportAsync(ownerId, "Original.txt", Bytes);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var blob = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Blobs.SingleAsync();
            var physicalPath = Path.Combine(host.Files.RootPath, blob.StorageKey.ToString().Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(physicalPath, Encoding.UTF8.GetBytes("corrupt"));
        }

        await Assert.ThrowsAsync<StorageIntegrityException>(() => host.ImportAsync(ownerId, "Retry.txt", Bytes));

        await using var inspection = host.Services.CreateAsyncScope();
        Assert.Equal(1, await inspection.ServiceProvider.GetRequiredService<NexoraDbContext>().Assets.CountAsync());
        Assert.Single(host.Files.Files);
    }

    [PostgresFact]
    public async Task CatalogRejectsAnAbsentOwnerAndContradictoryLengthForTheSameHash()
    {
        await using var host = await ContentTestHost.CreateAsync();
        var ownerId = await host.CreateOwnerAsync();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Bytes));
        await using var scope = host.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IContentCatalog>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.GetOrCreateBlobAsync(Guid.NewGuid(), hash,
            Bytes.Length, "application/octet-stream", DateTimeOffset.UtcNow, CancellationToken.None));
        var context = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await context.Blobs.CountAsync());

        _ = await catalog.GetOrCreateBlobAsync(ownerId, hash, Bytes.Length, "application/octet-stream", DateTimeOffset.UtcNow, CancellationToken.None);
        await Assert.ThrowsAsync<StorageIntegrityException>(() => catalog.GetOrCreateBlobAsync(ownerId, hash,
            Bytes.Length + 1, "application/octet-stream", DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(BlobState.Staging, (await context.Blobs.SingleAsync()).State);
        Assert.Empty(host.Files.Files);
    }

    [PostgresFact]
    public async Task OperationAndCleanupFailuresAreBothVisibleAndRetryRemainsSafe()
    {
        await using var host = await ContentTestHost.CreateAsync(failTemporaryCleanup: true);
        var ownerId = await host.CreateOwnerAsync();

        var error = await Assert.ThrowsAsync<AggregateException>(() => host.ImportAsync(ownerId, "Rejected.txt", Bytes, new string('0', 64)));

        Assert.Contains(error.InnerExceptions, exception => exception is StorageIntegrityException);
        Assert.Contains(error.InnerExceptions, exception => exception is IOException && exception.Message == "Simulated temporary cleanup failure.");
        Assert.Empty(host.Files.Files);
        var retry = await host.ImportAsync(ownerId, "Accepted.txt", Bytes);
        Assert.Equal(AssetImportStatus.Created, retry.Status);
        Assert.Single(host.Files.Files);
    }
}
