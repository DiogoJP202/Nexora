using Microsoft.Extensions.Options;
using Nexora.Application.Images;
using Nexora.Application.Jobs;
using Nexora.Application.Storage;
using Nexora.Application.Content;
using Nexora.Infrastructure.Configuration;
using Nexora.Infrastructure.Content;
using Npgsql;

namespace Nexora.Infrastructure.Images;

// A session lock protects a live renderer even after its durable job lease expires.
// A dedicated non-pooled connection releases the lock on disposal or connection loss.
public sealed class GuardedImageJobProcessor(IImageWorkStore store, IBlobStorage originals,
    IImageRenderer renderer, IDerivativeStorage derivatives, IOptions<DatabaseOptions> database) : IImageJobProcessor
{
    public async Task ProcessAsync(ImageWorkItem work, CancellationToken cancellationToken)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(database.Value.Nexora) { Pooling = false, CommandTimeout = 5 };
        await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock_shared(@scope, @key)", connection))
        {
            command.Parameters.AddWithValue("scope", BlobProcessingLock.Namespace);
            command.Parameters.AddWithValue("key", BlobProcessingLock.Key(work.Blob.Id));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        using var queries = new SemaphoreSlim(1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var monitorStop = new CancellationTokenSource();
        var monitor = MonitorAsync();
        try
        {
            await ValidateAsync(stop.Token);
            var processor = new ImageJobProcessor(store, originals,
                new FencedRenderer(renderer, ValidateAsync), new FencedDerivatives(derivatives, ValidateAsync));
            await processor.ProcessAsync(work, stop.Token);
        }
        finally
        {
            await monitorStop.CancelAsync();
            await monitor;
            // Explicit unlock is unnecessary: Pooling=false closes this exact session.
        }

        async Task ProbeAsync(CancellationToken token)
        {
            await queries.WaitAsync(token);
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = pg_backend_pid() AND locktype = 'advisory' AND mode = 'ShareLock' AND granted AND objsubid = 2)", connection);
                if (await command.ExecuteScalarAsync(token) is not true) throw new JobLeaseLostException();
            }
            finally { queries.Release(); }
        }
        async Task ValidateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await ProbeAsync(token);
            if (!await store.RenewAsync(work.Lease, token)) throw new JobLeaseLostException();
            token.ThrowIfCancellationRequested();
        }
        async Task MonitorAsync()
        {
            using var monitorToken = CancellationTokenSource.CreateLinkedTokenSource(monitorStop.Token, stop.Token);
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), monitorToken.Token);
                    await ProbeAsync(monitorToken.Token);
                }
            }
            catch (OperationCanceledException) when (monitorToken.IsCancellationRequested) { }
            catch { await stop.CancelAsync(); }
        }
    }

    private sealed class FencedRenderer(IImageRenderer inner, Func<CancellationToken, Task> validate) : IImageRenderer
    {
        public async Task<RenderedImage> RenderAsync(BlobDescriptor blob, Stream original, CancellationToken cancellationToken)
        {
            await validate(cancellationToken);
            return await inner.RenderAsync(blob, original, cancellationToken);
        }
    }
    private sealed class FencedDerivatives(IDerivativeStorage inner, Func<CancellationToken, Task> validate) : IDerivativeStorage
    {
        public async Task<DerivativeInfo> PublishAsync(DerivativeKey key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            await validate(cancellationToken);
            return await inner.PublishAsync(key, content, cancellationToken);
        }
        public Task<Stream> OpenReadAsync(DerivativeKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);
        public Task<DerivativeInfo?> GetInfoAsync(DerivativeKey key, CancellationToken cancellationToken) => inner.GetInfoAsync(key, cancellationToken);
        public Task CleanAttemptAsync(Guid generationId, bool preservePublished, CancellationToken cancellationToken) =>
            inner.CleanAttemptAsync(generationId, preservePublished, cancellationToken);
    }
}
