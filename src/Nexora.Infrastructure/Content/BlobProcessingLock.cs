using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Content;

internal static class BlobProcessingLock
{
    internal const int Namespace = 0x4E58494D;
    internal static int Key(Guid blobId) => BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(blobId.ToByteArray())) & int.MaxValue;

    internal static Task<bool> TryDeleteAsync(NexoraDbContext db, Guid blobId, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({Namespace}, {Key(blobId)}) AS \"Value\"")
            .SingleAsync(cancellationToken);
}
