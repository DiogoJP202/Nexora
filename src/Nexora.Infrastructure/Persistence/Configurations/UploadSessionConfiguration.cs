using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Authentication;
using Nexora.Domain.Content;
using Nexora.Domain.Uploads;
using Nexora.Infrastructure.Identity;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class UploadSessionConfiguration : IEntityTypeConfiguration<UploadSession>
{
    public void Configure(EntityTypeBuilder<UploadSession> builder)
    {
        builder.ToTable("UploadSessions", table =>
        {
            table.HasCheckConstraint("CK_UploadSessions_Length", "\"ExpectedLength\" BETWEEN 0 AND 4611686018427387903 AND \"ChunkSize\" BETWEEN 1 AND 67108864 AND \"ChunkCount\" BETWEEN 0 AND 65536 AND \"ChunkCount\" = (\"ExpectedLength\" + \"ChunkSize\" - 1) / \"ChunkSize\" AND \"ReservedBytes\" IN (0, \"ExpectedLength\" * 2)");
            table.HasCheckConstraint("CK_UploadSessions_State", "\"State\" IN ('Open', 'Finalizing', 'Completed', 'Cancelled', 'Expired', 'Failed')");
            table.HasCheckConstraint("CK_UploadSessions_Hash", "\"ExpectedSha256\" IS NULL OR \"ExpectedSha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_UploadSessions_Assembly", "(\"AssemblyTemporaryId\" IS NULL AND \"AssemblyLength\" IS NULL AND \"AssemblySha256\" IS NULL AND \"AssemblyMimeType\" IS NULL) OR (\"AssemblyTemporaryId\" IS NOT NULL AND \"AssemblyLength\" IS NOT NULL AND \"AssemblyLength\" = \"ExpectedLength\" AND \"AssemblySha256\" IS NOT NULL AND \"AssemblySha256\" ~ '^[0-9a-f]{64}$' AND \"AssemblyMimeType\" IS NOT NULL)");
            table.HasCheckConstraint("CK_UploadSessions_Result", "(\"State\" = 'Completed' AND ((\"ResultAssetId\" IS NOT NULL AND \"ResultPurgedAt\" IS NULL) OR (\"ResultAssetId\" IS NULL AND \"ResultPurgedAt\" IS NOT NULL AND \"ResultPurgedAt\" >= \"LastActivityAt\"))) OR (\"State\" <> 'Completed' AND \"ResultAssetId\" IS NULL AND \"ResultPurgedAt\" IS NULL)");
            table.HasCheckConstraint("CK_UploadSessions_Activity", "\"LastActivityAt\" >= \"CreatedAt\"");
        });
        builder.HasKey(upload => upload.Id);
        builder.Property(upload => upload.Id).ValueGeneratedNever();
        builder.Property(upload => upload.OriginalName).HasMaxLength(Asset.MaximumOriginalNameLength);
        builder.Property(upload => upload.ExpectedSha256).HasMaxLength(64).UseCollation("C");
        builder.Property(upload => upload.AssemblySha256).HasMaxLength(64).UseCollation("C");
        builder.Property(upload => upload.AssemblyMimeType).HasMaxLength(Blob.MaximumMimeTypeLength);
        builder.Property(upload => upload.FailureCode).HasMaxLength(64);
        builder.Property(upload => upload.State).HasConversion<string>().HasMaxLength(10);
        builder.HasOne<NexoraUser>().WithMany().HasForeignKey(upload => upload.OwnerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Device>().WithMany().HasForeignKey(upload => new { upload.OwnerId, upload.DeviceId })
            .HasPrincipalKey(device => new { device.UserId, device.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(upload => upload.ResultAsset).WithMany().HasForeignKey(upload => upload.ResultAssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(upload => new { upload.OwnerId, upload.State });
        builder.HasIndex(upload => new { upload.OwnerId, upload.ClientRequestId }).IsUnique()
            .HasFilter("\"ClientRequestId\" IS NOT NULL");
        builder.HasIndex(upload => new { upload.State, upload.LastActivityAt });
        builder.HasIndex(upload => upload.AssemblyTemporaryId).IsUnique();
    }
}
