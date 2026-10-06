using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Domain.Content;

namespace Nexora.IntegrationTests;

public sealed class StorageFilesystemTests
{
    [Fact]
    public async Task NonseekableContentRoundTripsThroughBoundedTemporaryStorage()
    {
        using var files = new StorageTestFiles();
        var storage = files.Temporary;
        var bytes = CreateContent();
        await using var source = new StorageTestReadStream(bytes);
        var created = await storage.CreateAsync(source, bytes.Length, CancellationToken.None);

        Assert.True(source.CanRead);
        Assert.Equal(bytes.Length, created.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), created.Sha256);
        Assert.Equal("application/octet-stream", created.DetectedMimeType);
        var info = await storage.GetInfoAsync(created.Key, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(created, info);
        await using (var input = await storage.OpenReadAsync(created.Key, CancellationToken.None))
        {
            await StreamingContentHash.VerifyAsync(input, created.Length, created.Sha256, CancellationToken.None);
        }

        await storage.DeleteAsync(created.Key, CancellationToken.None);
        await storage.DeleteAsync(created.Key, CancellationToken.None);
        Assert.Null(await storage.GetInfoAsync(created.Key, CancellationToken.None));
        Assert.Empty(files.Files);
    }

    [Fact]
    public async Task ImmutablePublicationCanBeRepeatedButCannotReplaceExistingBytes()
    {
        using var files = new StorageTestFiles();
        var storage = files.Blobs;
        var key = new BlobStorageKey(Guid.NewGuid());
        var bytes = Encoding.UTF8.GetBytes("hello");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var source = new StorageTestReadStream(bytes);
        Assert.Equal(BlobPublicationResult.Published,
            await storage.PublishAsync(key, source, bytes.Length, hash, CancellationToken.None));
        Assert.True(source.CanRead);
        await using var duplicate = new StorageTestReadStream(bytes);
        Assert.Equal(BlobPublicationResult.AlreadyExists,
            await storage.PublishAsync(key, duplicate, bytes.Length, hash, CancellationToken.None));
        var replacement = Encoding.UTF8.GetBytes("world");
        await using var conflicting = new StorageTestReadStream(replacement);
        await Assert.ThrowsAsync<StorageIntegrityException>(() => storage.PublishAsync(key, conflicting,
            replacement.Length, Convert.ToHexStringLower(SHA256.HashData(replacement)), CancellationToken.None));

        var info = await storage.GetInfoAsync(key, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(bytes.Length, info.Length);
        await using (var input = await storage.OpenReadAsync(key, CancellationToken.None))
        {
            await StreamingContentHash.VerifyAsync(input, bytes.Length, hash, CancellationToken.None);
        }

        Assert.Single(files.Files);
        await storage.DeleteAsync(key, CancellationToken.None);
        await storage.DeleteAsync(key, CancellationToken.None);
        Assert.Null(await storage.GetInfoAsync(key, CancellationToken.None));
        Assert.Empty(files.Files);
    }

    [Theory]
    [InlineData(4, "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    [InlineData(6, "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    [InlineData(5, "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task WrongPublicationIdentityLeavesNeitherObjectNorPartialFile(long expectedLength, string hash)
    {
        using var files = new StorageTestFiles();
        var storage = files.Blobs;
        var key = new BlobStorageKey(Guid.NewGuid());
        await using var source = new StorageTestReadStream(Encoding.UTF8.GetBytes("hello"));

        await Assert.ThrowsAsync<StorageIntegrityException>(() =>
            storage.PublishAsync(key, source, expectedLength, hash, CancellationToken.None));

        Assert.Null(await storage.GetInfoAsync(key, CancellationToken.None));
        Assert.Empty(files.Files);
    }

    [Fact]
    public async Task TemporaryLengthLimitIsEnforcedAgainstTheActualSource()
    {
        using var files = new StorageTestFiles();
        await using var source = new StorageTestReadStream(CreateContent());

        await Assert.ThrowsAsync<StorageLimitExceededException>(() =>
            files.Temporary.CreateAsync(source, 65_536, CancellationToken.None));

        Assert.Empty(files.Files);
    }

    [Fact]
    public async Task FailedSourceReadCleansBothTemporaryAndPublicationPartials()
    {
        using var files = new StorageTestFiles();
        var bytes = CreateContent();
        await using var temporarySource = new StorageTestReadStream(bytes, failAfterReads: 1);
        await Assert.ThrowsAnyAsync<IOException>(() => files.Temporary.CreateAsync(temporarySource, bytes.Length, CancellationToken.None));
        Assert.Empty(files.Files);

        var key = new BlobStorageKey(Guid.NewGuid());
        await using var blobSource = new StorageTestReadStream(bytes, failAfterReads: 1);
        await Assert.ThrowsAnyAsync<IOException>(() => files.Blobs.PublishAsync(key, blobSource, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), CancellationToken.None));
        Assert.Empty(files.Files);
    }

    [Fact]
    public async Task CancellationDuringReadingCleansBothTemporaryAndPublicationPartials()
    {
        using var files = new StorageTestFiles();
        var bytes = CreateContent();
        using var temporaryCancellation = new CancellationTokenSource();
        await using var temporarySource = new StorageTestReadStream(bytes, cancelAfterRead: temporaryCancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            files.Temporary.CreateAsync(temporarySource, bytes.Length, temporaryCancellation.Token));
        Assert.Empty(files.Files);

        using var blobCancellation = new CancellationTokenSource();
        await using var blobSource = new StorageTestReadStream(bytes, cancelAfterRead: blobCancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.Blobs.PublishAsync(new BlobStorageKey(Guid.NewGuid()),
            blobSource, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), blobCancellation.Token));
        Assert.Empty(files.Files);
    }

    public static IEnumerable<object[]> ImageSignatures()
    {
        yield return [new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01 }, "image/png"];
        yield return [new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0x00 }, "image/jpeg"];
        yield return [new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp"];
    }

    [Theory]
    [MemberData(nameof(ImageSignatures))]
    public async Task PreliminaryMimeDetectionUsesBytesRatherThanAClientFilename(byte[] signature, string expectedMime)
    {
        using var files = new StorageTestFiles();
        await using var source = new StorageTestReadStream(signature);
        var info = await files.Temporary.CreateAsync(source, signature.Length, CancellationToken.None);

        Assert.Equal(expectedMime, info.DetectedMimeType);
        await files.Temporary.DeleteAsync(info.Key, CancellationToken.None);
        Assert.Empty(files.Files);
    }

    [Fact]
    public async Task LinkedBlobDirectoryCannotPublishReadMetadataOrDeleteOutsideTheStorageRoot()
    {
        using var files = new StorageTestFiles();
        using var outside = new StorageTestFiles();
        var linkedDirectory = Path.Combine(files.RootPath, "blobs");
        var sentinel = Path.Combine(outside.RootPath, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "Keep outside content intact.");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // The arguments are two fixture-owned paths. Reject shell metacharacters
                // instead of allowing a user's TEMP path to become a cmd expression.
                foreach (var path in new[] { linkedDirectory, outside.RootPath })
                {
                    Assert.False(path.Any(character => "%!&|<>^\"()\r\n".Contains(character)),
                        "O caminho de TEMP não pode conter metacaracteres para este teste de junction.");
                }

                var start = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[] { "/d", "/c", "mklink", "/J", linkedDirectory, outside.RootPath })
                {
                    start.ArgumentList.Add(argument);
                }

                using var process = new Process { StartInfo = start };
                Assert.True(process.Start());
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(deadline.Token);
                    await Task.WhenAll(output, error).WaitAsync(deadline.Token);
                    Assert.Equal(0, process.ExitCode);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            else
            {
                Directory.CreateSymbolicLink(linkedDirectory, outside.RootPath);
            }

            Assert.True(File.GetAttributes(linkedDirectory).HasFlag(FileAttributes.ReparsePoint));
            var storage = files.Blobs;
            var key = new BlobStorageKey(Guid.NewGuid());
            await using var source = new StorageTestReadStream(Encoding.UTF8.GetBytes("hello"));
            await Assert.ThrowsAsync<UnsafeStoragePathException>(() => storage.PublishAsync(key, source, 5,
                "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", CancellationToken.None));
            await Assert.ThrowsAsync<UnsafeStoragePathException>(() => storage.GetInfoAsync(key, CancellationToken.None));
            await Assert.ThrowsAsync<UnsafeStoragePathException>(() => storage.DeleteAsync(key, CancellationToken.None));
            Assert.Equal("Keep outside content intact.", await File.ReadAllTextAsync(sentinel));
            Assert.Single(outside.Files);
        }
        finally
        {
            if (Directory.Exists(linkedDirectory))
            {
                Assert.True(File.GetAttributes(linkedDirectory).HasFlag(FileAttributes.ReparsePoint));
                // Unlink just the fixture's link before any recursive fixture cleanup.
                Directory.Delete(linkedDirectory, recursive: false);
            }
        }
    }

    private static byte[] CreateContent() => Enumerable.Range(0, 128 * 1024 + 17).Select(index => (byte)(index % 251)).ToArray();
}
