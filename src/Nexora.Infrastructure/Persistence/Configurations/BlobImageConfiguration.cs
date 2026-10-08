using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Images;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class BlobImageConfiguration : IEntityTypeConfiguration<BlobImage>
{
    public void Configure(EntityTypeBuilder<BlobImage> builder)
    {
        builder.ToTable("BlobImages", table =>
        {
            table.HasCheckConstraint("CK_BlobImages_State", "\"State\" IN ('Pending', 'Processing', 'Ready', 'Failed')");
            table.HasCheckConstraint("CK_BlobImages_Reservation", "\"ReservedBytes\" >= 0 AND (\"State\" <> 'Processing' OR \"ReservedBytes\" > 0)");
            table.HasCheckConstraint("CK_BlobImages_Metadata", "(\"State\" = 'Ready' AND \"Width\" IS NOT NULL AND \"Width\" > 0 AND \"Height\" IS NOT NULL AND \"Height\" > 0 AND \"ProcessedAt\" IS NOT NULL AND \"DerivativeGenerationId\" IS NOT NULL AND \"ThumbnailLength\" IS NOT NULL AND \"ThumbnailLength\" > 0 AND \"PreviewLength\" IS NOT NULL AND \"PreviewLength\" > 0 AND \"ThumbnailSha256\" IS NOT NULL AND \"ThumbnailSha256\" ~ '^[0-9a-f]{64}$' AND \"PreviewSha256\" IS NOT NULL AND \"PreviewSha256\" ~ '^[0-9a-f]{64}$') OR (\"State\" <> 'Ready' AND \"Width\" IS NULL AND \"Height\" IS NULL AND \"ProcessedAt\" IS NULL AND \"DerivativeGenerationId\" IS NULL AND \"ThumbnailLength\" IS NULL AND \"PreviewLength\" IS NULL AND \"ThumbnailSha256\" IS NULL AND \"PreviewSha256\" IS NULL AND \"CapturedAtLocal\" IS NULL AND \"CapturedAtUtc\" IS NULL)");
        });
        builder.HasKey(image => image.BlobId);
        builder.Property(image => image.BlobId).ValueGeneratedNever();
        builder.Property(image => image.State).HasConversion<string>().HasMaxLength(10);
        builder.Property(image => image.CapturedAtLocal).HasColumnType("timestamp without time zone");
        builder.Property(image => image.ThumbnailSha256).HasMaxLength(64).UseCollation("C");
        builder.Property(image => image.PreviewSha256).HasMaxLength(64).UseCollation("C");
        builder.Property(image => image.FailureCode).HasMaxLength(64);
        builder.HasOne(image => image.Blob).WithOne(blob => blob.Image).HasForeignKey<BlobImage>(image => image.BlobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(image => image.DerivativeGenerationId).IsUnique();
        builder.HasIndex(image => image.State);
    }
}
