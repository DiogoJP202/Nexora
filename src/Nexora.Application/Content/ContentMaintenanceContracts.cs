namespace Nexora.Application.Content;

public interface IContentMaintenance
{
    Task<int> PurgeExpiredAssetsAsync(int limit, CancellationToken cancellationToken);
    Task<int> CollectBlobsAsync(int limit, CancellationToken cancellationToken);
}
