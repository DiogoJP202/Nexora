using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Uploads;

internal static class UploadTransactionLock
{
    internal static long Key(Guid uploadId)
    {
        Span<byte> identifier = stackalloc byte[16];
        uploadId.TryWriteBytes(identifier, bigEndian: true, out _);
        return BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(identifier));
    }

    internal static Task AcquireAsync(NexoraDbContext db, Guid uploadId, CancellationToken cancellationToken)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({Key(uploadId)})", cancellationToken);

    internal static Task<bool> TryAcquireAsync(NexoraDbContext db, Guid uploadId, CancellationToken cancellationToken)
        => db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({Key(uploadId)}) AS \"Value\"")
            .SingleAsync(cancellationToken);

    internal static Task<UploadSession?> RowAsync(NexoraDbContext db, Guid uploadId, CancellationToken cancellationToken)
        => db.UploadSessions.FromSqlInterpolated($"SELECT * FROM \"UploadSessions\" WHERE \"Id\" = {uploadId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
}
