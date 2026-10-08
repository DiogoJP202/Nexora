using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Application.Uploads;
using Nexora.Infrastructure;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Persistence;
using Nexora.Worker;

namespace Nexora.IntegrationTests;

public sealed class WorkerCoordinatorTests
{
    [PostgresFact]
    public async Task Real_worker_renews_lease_in_another_scope_during_long_processing()
    {
        await using var fixture = await FileApiTestHost.CreateAsync();
        var tokens = await fixture.LoginAsync();
        byte[] data = [1, 2, 3, 4];
        var upload = await fixture.CreateUploadAsync(tokens.AccessToken, data);
        await fixture.SendChunksAsync(tokens.AccessToken, upload, data);
        using (var accepted = await fixture.SendAsync(HttpMethod.Post, $"/api/uploads/{upload.Id}/complete", tokens.AccessToken))
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Nexora"] = fixture.Database.ConnectionString,
            ["Storage:RootPath"] = fixture.Files.RootPath,
            ["Uploads:LeaseDuration"] = "00:00:06",
            ["Uploads:LeaseRenewInterval"] = "00:00:02",
            ["Uploads:PollInterval"] = "00:00:00.100",
            ["Uploads:MaintenanceInterval"] = "00:00:01",
            ["Uploads:ProcessingTimeout"] = "00:00:20"
        }).Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration);
        services.RemoveAll<IUploadJobProcessor>();
        services.AddScoped<IUploadJobProcessor>(provider => new SlowProcessor(new UploadJobProcessor(
            provider.GetRequiredService<ITrackedTemporaryStorage>(), provider.GetRequiredService<IUploadWorkStore>(),
            provider.GetRequiredService<IStagedAssetIngestionService>()), started, finished));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var worker = new UploadWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<UploadOptions>>(), TimeProvider.System, NullLogger<UploadWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            async Task<DateTimeOffset?> ReadExpiryAsync()
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().BackgroundJobs.AsNoTracking()
                    .Where(job => job.UploadSessionId == upload.Id).Select(job => job.LeaseExpiresAt).SingleAsync();
            }
            var initial = await ReadExpiryAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(2700));
            var renewed = await ReadExpiryAsync();
            Assert.True(renewed > initial, "O coordinator deve renovar no banco enquanto o processor aguarda.");
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var completed = await fixture.GetUploadAsync(tokens.AccessToken, upload.Id);
            Assert.Equal(Domain.Uploads.UploadState.Completed, completed.State);
            Assert.NotNull(completed.Result);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(stop.Token);
        }
    }

    private sealed class SlowProcessor(IUploadJobProcessor actual, TaskCompletionSource started,
        TaskCompletionSource finished) : IUploadJobProcessor
    {
        public async Task ProcessAsync(UploadWorkItem work, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
                await actual.ProcessAsync(work, cancellationToken);
                finished.TrySetResult();
            }
            catch (Exception failure)
            {
                finished.TrySetException(failure);
                throw;
            }
        }
    }
}
