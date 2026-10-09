using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Content;
using Nexora.Infrastructure.Identity;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class AssetSyncStateConfiguration : IEntityTypeConfiguration<AssetSyncState>
{
    public void Configure(EntityTypeBuilder<AssetSyncState> builder)
    {
        builder.ToTable("AssetSyncStates", table => table.HasCheckConstraint("CK_AssetSyncStates_Sequence", "\"Sequence\" >= 0"));
        builder.HasKey(state => state.OwnerId);
        builder.Property(state => state.OwnerId).ValueGeneratedNever();
        builder.Property(state => state.Epoch).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(state => state.Sequence).HasDefaultValue(0L);
        builder.HasOne<NexoraUser>().WithOne().HasForeignKey<AssetSyncState>(state => state.OwnerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AssetSyncEntryConfiguration : IEntityTypeConfiguration<AssetSyncEntry>
{
    public void Configure(EntityTypeBuilder<AssetSyncEntry> builder)
    {
        builder.ToTable("AssetSyncEntries", table =>
        {
            table.HasCheckConstraint("CK_AssetSyncEntries_Sequence", "\"Sequence\" > 0");
            table.HasCheckConstraint("CK_AssetSyncEntries_Payload", "(\"Kind\" = 'upsert' AND \"Payload\" IS NOT NULL) OR (\"Kind\" = 'purge' AND \"Payload\" IS NULL)");
        });
        builder.HasKey(entry => new { entry.OwnerId, entry.Sequence });
        builder.Property(entry => entry.Sequence).ValueGeneratedNever();
        builder.Property(entry => entry.Kind).HasMaxLength(6);
        builder.Property(entry => entry.Payload).HasColumnType("jsonb");
        builder.HasOne<AssetSyncState>().WithMany().HasForeignKey(entry => entry.OwnerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(entry => new { entry.OwnerId, entry.AssetId, entry.Sequence }).IsDescending(false, false, true);
    }
}
