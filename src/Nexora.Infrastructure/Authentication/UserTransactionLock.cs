using Microsoft.EntityFrameworkCore;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Authentication;

internal static class UserTransactionLock
{
    public static Task<NexoraUser?> AcquireAsync(NexoraDbContext context, Guid userId, CancellationToken cancellationToken)
        => context.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
}
