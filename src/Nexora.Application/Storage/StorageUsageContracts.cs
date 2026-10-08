namespace Nexora.Application.Storage;

public sealed record StorageUsage(long TotalBytes, long AvailableBytes, long BlobBytes,
    long DerivativeBytes, long TemporaryBytes);

public interface IStorageUsageReader
{
    Task<StorageUsage> ReadAsync(CancellationToken cancellationToken);
}

public interface IStorageHousekeeping
{
    Task<int> RemoveOrphansAsync(IReadOnlySet<Guid> referencedTemporaryIds, DateTimeOffset olderThan,
        CancellationToken cancellationToken);
}

public sealed record StorageStatusSnapshot(long ActiveLibraryBytes, long TrashBytes, long BlobBytes,
    long DerivativeBytes, long TemporaryBytes, long ReservedBytes, long TotalBytes, long AvailableBytes);

public interface IStorageStatusService
{
    Task<StorageStatusSnapshot> GetAsync(Guid ownerId, CancellationToken cancellationToken);
}
