using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Uploads;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class UploadChunkConfiguration : IEntityTypeConfiguration<UploadChunk>
{
    public void Configure(EntityTypeBuilder<UploadChunk> builder)
    {
        builder.ToTable("UploadChunks", table =>
        {
            table.HasCheckConstraint("CK_UploadChunks_NumberSize", "\"Number\" >= 0 AND \"Size\" > 0");
            table.HasCheckConstraint("CK_UploadChunks_Hash", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
        });
        builder.HasKey(chunk => new { chunk.UploadSessionId, chunk.Number });
        builder.Property(chunk => chunk.Sha256).HasMaxLength(64).UseCollation("C");
        builder.HasOne(chunk => chunk.Session).WithMany(upload => upload.Chunks).HasForeignKey(chunk => chunk.UploadSessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(chunk => chunk.TemporaryId).IsUnique();
    }
}
