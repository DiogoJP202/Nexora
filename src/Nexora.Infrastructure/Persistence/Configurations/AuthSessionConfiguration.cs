using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Authentication;
using Nexora.Infrastructure.Identity;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class AuthSessionConfiguration : IEntityTypeConfiguration<AuthSession>
{
    public void Configure(EntityTypeBuilder<AuthSession> builder)
    {
        builder.ToTable("AuthSessions", table =>
            table.HasCheckConstraint("CK_AuthSessions_Expiration", "\"ExpiresAt\" > \"CreatedAt\""));
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedNever();
        builder.Property(session => session.SecurityStamp).HasMaxLength(256);
        builder.HasOne<NexoraUser>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(session => session.Device).WithMany(device => device.Sessions)
            .HasForeignKey(session => new { session.UserId, session.DeviceId })
            .HasPrincipalKey(device => new { device.UserId, device.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(session => new { session.UserId, session.DeviceId });
        builder.HasIndex(session => session.ExpiresAt);
    }
}
