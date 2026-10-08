using Microsoft.EntityFrameworkCore;
using Nexora.Application.Storage;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Content;

public sealed class PostgresStorageStatusService(NexoraDbContext db, IStorageUsageReader usageReader) : IStorageStatusService
{
    public async Task<StorageStatusSnapshot> GetAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var active = await db.Assets.AsNoTracking().Where(asset => asset.OwnerId == ownerId && asset.DeletedAt == null)
            .Select(asset => (long?)asset.Blob.Size).SumAsync(cancellationToken) ?? 0;
        var trash = await db.Assets.AsNoTracking().Where(asset => asset.OwnerId == ownerId && asset.DeletedAt != null)
            .Select(asset => (long?)asset.Blob.Size).SumAsync(cancellationToken) ?? 0;
        var reserved = await db.UploadSessions.AsNoTracking().Select(upload => (long?)upload.ReservedBytes)
            .SumAsync(cancellationToken) ?? 0;
        var physical = await usageReader.ReadAsync(cancellationToken);
        return new StorageStatusSnapshot(active, trash, physical.BlobBytes, physical.DerivativeBytes,
            physical.TemporaryBytes, reserved, physical.TotalBytes, physical.AvailableBytes);
    }
}
