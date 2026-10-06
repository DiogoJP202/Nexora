using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Content;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class BlobConfiguration : IEntityTypeConfiguration<Blob>
{
    public void Configure(EntityTypeBuilder<Blob> builder)
    {
        builder.ToTable("Blobs", table =>
        {
            table.HasCheckConstraint("CK_Blobs_Size", "\"Size\" >= 0");
            table.HasCheckConstraint("CK_Blobs_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_Blobs_State", "\"State\" IN ('Staging', 'Ready', 'Deleting')");
            table.HasCheckConstraint("CK_Blobs_StorageKey", "\"StorageKey\" = 'blobs/' || substring(replace(\"Id\"::text, '-', ''), 1, 2) || '/' || substring(replace(\"Id\"::text, '-', ''), 3, 2) || '/' || replace(\"Id\"::text, '-', '')");
        });
        builder.HasKey(blob => blob.Id);
        builder.Property(blob => blob.Id).ValueGeneratedNever();
        builder.Property(blob => blob.Sha256).HasMaxLength(Blob.Sha256Length).UseCollation("C");
        builder.Property(blob => blob.DetectedMimeType).HasMaxLength(Blob.MaximumMimeTypeLength);
        builder.Property(blob => blob.StorageKey).HasConversion(key => key.ToString(), value => BlobStorageKey.Parse(value)).HasMaxLength(44);
        builder.Property(blob => blob.State).HasConversion<string>().HasMaxLength(8);
        builder.HasIndex(blob => blob.Sha256).IsUnique();
    }
}
