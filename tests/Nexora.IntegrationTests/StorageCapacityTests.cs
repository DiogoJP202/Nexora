using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nexora.Application.Storage;
using Nexora.Domain.Content;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Storage;

namespace Nexora.IntegrationTests;

public sealed class StorageCapacityTests
{
    [Fact]
    public async Task Inventory_counts_physical_content_and_never_creates_missing_categories()
    {
        using var files = new StorageTestFiles();
        var options = Options.Create(new StorageOptions { RootPath = files.RootPath });
        var reader = new LocalStorageUsageReader(options);
        var empty = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(0, empty.BlobBytes);
        Assert.Equal(0, empty.TemporaryBytes);
        Assert.True(empty.TotalBytes >= empty.AvailableBytes);
        Assert.Empty(Directory.GetFileSystemEntries(files.RootPath));
        byte[] bytes = [1, 2, 3, 4, 5];
        using var source = new MemoryStream(bytes);
        var temporary = await files.Temporary.CreateAsync(source, bytes.Length, CancellationToken.None);
        using var publication = new MemoryStream(bytes);
        await files.Blobs.PublishAsync(new BlobStorageKey(Guid.NewGuid()), publication, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), CancellationToken.None);
        var occupied = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(bytes.Length, occupied.BlobBytes);
        Assert.Equal(bytes.Length, occupied.TemporaryBytes);
        Assert.Equal(0, occupied.DerivativeBytes);
        await files.Temporary.DeleteAsync(temporary.Key, CancellationToken.None);
    }

    [Fact]
    public async Task Housekeeping_removes_only_old_unreferenced_generated_objects()
    {
        using var files = new StorageTestFiles();
        var options = Options.Create(new StorageOptions { RootPath = files.RootPath });
        var keep = await files.Temporary.CreateAsync(new MemoryStream([1]), 1, CancellationToken.None);
        var orphan = await files.Temporary.CreateAsync(new MemoryStream([2]), 1, CancellationToken.None);
        var fresh = await files.Temporary.CreateAsync(new MemoryStream([3]), 1, CancellationToken.None);
        foreach (var key in new[] { keep.Key, orphan.Key })
            File.SetLastWriteTimeUtc(Path.Combine(files.RootPath, "temp", key + ".chunk"), DateTime.UtcNow.AddDays(-2));
        var unrelated = Path.Combine(files.RootPath, "temp", "operator-notes.txt");
        await File.WriteAllTextAsync(unrelated, "test-only");
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-2));
        var cleanup = new LocalStorageHousekeeping(options);
        Assert.Equal(1, await cleanup.RemoveOrphansAsync(new HashSet<Guid> { keep.Key.Id },
            DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));
        Assert.NotNull(await files.Temporary.GetInfoAsync(keep.Key, CancellationToken.None));
        Assert.NotNull(await files.Temporary.GetInfoAsync(fresh.Key, CancellationToken.None));
        Assert.Null(await files.Temporary.GetInfoAsync(orphan.Key, CancellationToken.None));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Attempt_cleanup_refuses_an_active_writer_and_preserves_committed_assembly()
    {
        using var files = new StorageTestFiles();
        var key = new TemporaryObjectKey(Guid.NewGuid());
        await files.Temporary.CreateWithKeyAsync(key, new MemoryStream([1, 2]), 2, CancellationToken.None);
        var partial = Path.Combine(files.RootPath, "temp", key + ".publishing");
        using (var writer = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await writer.WriteAsync(new byte[] { 3 });
            await writer.FlushAsync();
            await Assert.ThrowsAsync<IOException>(() => files.Temporary.DeleteAttemptAsync(key, true, CancellationToken.None));
            Assert.True(File.Exists(partial));
        }
        await files.Temporary.DeleteAttemptAsync(key, true, CancellationToken.None);
        Assert.False(File.Exists(partial));
        Assert.NotNull(await files.Temporary.GetInfoAsync(key, CancellationToken.None));
        await files.Temporary.DeleteAttemptAsync(key, false, CancellationToken.None);
        Assert.Empty(files.Files);
    }

    [Fact]
    public void Invalid_upload_limits_and_timer_policies_are_refused()
    {
        var validator = new UploadOptionsValidator();
        Assert.True(validator.Validate(null, new UploadOptions()).Succeeded);
        Assert.True(validator.Validate(null, new UploadOptions { ChunkSizeBytes = 0 }).Failed);
        Assert.True(validator.Validate(null, new UploadOptions { MinimumFreeBytes = -1 }).Failed);
        Assert.True(validator.Validate(null, new UploadOptions { MaximumJobAttempts = 0 }).Failed);
        Assert.True(validator.Validate(null, new UploadOptions { LeaseRenewInterval = TimeSpan.FromMinutes(1) }).Failed);
        Assert.True(validator.Validate(null, new UploadOptions { ProcessingTimeout = TimeSpan.FromDays(90) }).Failed);
        Assert.True(validator.Validate(null, new UploadOptions { OrphanGracePeriod = TimeSpan.FromMinutes(5) }).Failed);
        Assert.True(validator.Validate(null, new UploadOptions { ChunkSizeBytes = 1 }).Failed);
    }
}
