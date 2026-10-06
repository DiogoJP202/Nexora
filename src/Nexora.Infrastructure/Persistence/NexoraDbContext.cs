using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Nexora.Domain.Authentication;
using Nexora.Domain.Content;
using Nexora.Infrastructure.Identity;

namespace Nexora.Infrastructure.Persistence;

public sealed class NexoraDbContext(DbContextOptions<NexoraDbContext> options)
    : IdentityDbContext<NexoraUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<Asset> Assets => Set<Asset>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<NexoraUser>().HasIndex(user => user.NormalizedEmail).IsUnique().HasDatabaseName("EmailIndex");
        builder.ApplyConfigurationsFromAssembly(typeof(NexoraDbContext).Assembly);
    }
}
