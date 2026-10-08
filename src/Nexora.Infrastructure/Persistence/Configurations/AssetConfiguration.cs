using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Content;
using Nexora.Infrastructure.Identity;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("Assets", table => table.HasCheckConstraint("CK_Assets_DeletedAt", "\"DeletedAt\" IS NULL OR \"DeletedAt\" >= \"UploadedAt\""));
        builder.HasKey(asset => asset.Id);
        builder.Property(asset => asset.Id).ValueGeneratedNever();
        builder.Property(asset => asset.OriginalName).HasMaxLength(Asset.MaximumOriginalNameLength);
        builder.HasOne<NexoraUser>().WithMany().HasForeignKey(asset => asset.OwnerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(asset => asset.Blob).WithMany(blob => blob.Assets).HasForeignKey(asset => asset.BlobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(asset => new { asset.OwnerId, asset.BlobId }).IsUnique();
        builder.HasIndex(asset => new { asset.OwnerId, asset.UploadedAt, asset.Id }).IsDescending(false, true, true);
        builder.HasIndex(asset => new { asset.OwnerId, asset.DeletedAt, asset.Id }).IsDescending(false, true, true);
        builder.HasIndex(asset => new { asset.DeletedAt, asset.Id }).HasFilter("\"DeletedAt\" IS NOT NULL");
        builder.HasIndex(asset => new { asset.OwnerId, asset.IsFavorite, asset.UploadedAt, asset.Id })
            .IsDescending(false, false, true, true).HasFilter("\"DeletedAt\" IS NULL");
    }
}
