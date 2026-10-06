using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Application.Content;
using Nexora.Application.Storage;
using Nexora.Domain.Content;
using Nexora.Infrastructure;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

internal sealed class ContentTestHost : IAsyncDisposable
{
    private readonly IConfigurationRoot _configuration;

    private ContentTestHost(PostgresTestDatabase database, StorageTestFiles files, bool failAfterPublication, bool failTemporaryCleanup)
    {
        Database = database;
        Files = files;
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] = database.ConnectionString,
            ["Storage:RootPath"] = files.RootPath
        }).Build();
        var services = new ServiceCollection().AddLogging().AddInfrastructure(_configuration);
        if (failAfterPublication)
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage>(new PublicationFailureStorage(files.Blobs));
        }

        if (failTemporaryCleanup)
        {
            services.RemoveAll<ITemporaryStorage>();
            services.AddSingleton<ITemporaryStorage>(new CleanupFailureStorage(files.Temporary));
        }

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public PostgresTestDatabase Database { get; }
    public StorageTestFiles Files { get; }
    public ServiceProvider Services { get; }

    public static async Task<ContentTestHost> CreateAsync(bool failAfterPublication = false, bool failTemporaryCleanup = false)
    {
        var database = await PostgresTestDatabase.CreateAsync();
        var files = new StorageTestFiles();
        ContentTestHost? host = null;
        try
        {
            host = new ContentTestHost(database, files, failAfterPublication, failTemporaryCleanup);
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
        var userId = Guid.NewGuid();
        var email = userId.ToString("N") + "@nexora.test";
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<NexoraUser>>().CreateAsync(new NexoraUser
        {
            Id = userId, UserName = email, Email = email
        }, "Test-owner-password-8612!");
        Assert.True(result.Succeeded, "A conta isolada de teste deve ser criada.");
        return userId;
    }

    public async Task<AssetImportResult> ImportAsync(Guid ownerId, string name, byte[] content, string? expectedHash = null)
    {
        await using var scope = Services.CreateAsyncScope();
        await using var input = new StorageTestReadStream(content);
        return await scope.ServiceProvider.GetRequiredService<IAssetIngestionService>()
            .ImportAsync(new AssetImportRequest(ownerId, name, content.Length, expectedHash), input, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        (_configuration as IDisposable)?.Dispose();
        Files.Dispose();
        await Database.DisposeAsync();
    }

    private sealed class PublicationFailureStorage(IBlobStorage actual) : IBlobStorage
    {
        private int _remainingFailures = 1;

        public async Task<BlobPublicationResult> PublishAsync(BlobStorageKey key, Stream content, long expectedLength,
            string expectedSha256, CancellationToken cancellationToken)
        {
            var result = await actual.PublishAsync(key, content, expectedLength, expectedSha256, cancellationToken);
            if (Interlocked.Exchange(ref _remainingFailures, 0) == 1)
            {
                throw new IOException("Simulated failure after immutable physical publication.");
            }

            return result;
        }

        public Task<Stream> OpenReadAsync(BlobStorageKey key, CancellationToken cancellationToken) => actual.OpenReadAsync(key, cancellationToken);
        public Task<BlobObjectInfo?> GetInfoAsync(BlobStorageKey key, CancellationToken cancellationToken) => actual.GetInfoAsync(key, cancellationToken);
        public Task DeleteAsync(BlobStorageKey key, CancellationToken cancellationToken) => actual.DeleteAsync(key, cancellationToken);
    }

    private sealed class CleanupFailureStorage(ITemporaryStorage actual) : ITemporaryStorage
    {
        private int _remainingFailures = 1;
        public Task<TemporaryObjectInfo> CreateAsync(Stream content, long maximumLength, CancellationToken cancellationToken)
            => actual.CreateAsync(content, maximumLength, cancellationToken);
        public Task<Stream> OpenReadAsync(TemporaryObjectKey key, CancellationToken cancellationToken) => actual.OpenReadAsync(key, cancellationToken);
        public Task<TemporaryObjectInfo?> GetInfoAsync(TemporaryObjectKey key, CancellationToken cancellationToken) => actual.GetInfoAsync(key, cancellationToken);
        public async Task DeleteAsync(TemporaryObjectKey key, CancellationToken cancellationToken)
        {
            await actual.DeleteAsync(key, cancellationToken);
            if (Interlocked.Exchange(ref _remainingFailures, 0) == 1)
            {
                throw new IOException("Simulated temporary cleanup failure.");
            }
        }
    }
}
