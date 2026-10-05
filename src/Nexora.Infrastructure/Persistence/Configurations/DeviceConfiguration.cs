using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Authentication;
using Nexora.Infrastructure.Identity;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("Devices");
        builder.HasKey(device => device.Id);
        builder.Property(device => device.Id).ValueGeneratedNever();
        builder.Property(device => device.Name).HasMaxLength(Device.MaximumNameLength);
        builder.Property(device => device.Platform).HasMaxLength(Device.MaximumPlatformLength);
        builder.HasAlternateKey(device => new { device.UserId, device.Id });
        builder.HasOne<NexoraUser>().WithMany().HasForeignKey(device => device.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(device => new { device.UserId, device.CreatedAt });
    }
}
