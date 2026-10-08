using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Jobs;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> builder)
    {
        builder.ToTable("BackgroundJobs", table =>
        {
            table.HasCheckConstraint("CK_BackgroundJobs_Kind", "\"Kind\" = 'FinalizeUpload'");
            table.HasCheckConstraint("CK_BackgroundJobs_State", "\"State\" IN ('Pending', 'Running', 'Succeeded', 'Failed', 'Cancelled')");
            table.HasCheckConstraint("CK_BackgroundJobs_Attempts", "\"Attempts\" >= 0 AND \"MaximumAttempts\" BETWEEN 1 AND 100 AND \"Attempts\" <= \"MaximumAttempts\"");
            table.HasCheckConstraint("CK_BackgroundJobs_Lease", "\"State\" <> 'Running' OR (\"LeaseToken\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL)");
        });
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).ValueGeneratedNever();
        builder.Property(job => job.Kind).HasMaxLength(32);
        builder.Property(job => job.State).HasConversion<string>().HasMaxLength(9);
        builder.Property(job => job.FailureCode).HasMaxLength(64);
        builder.HasOne(job => job.Upload).WithOne(upload => upload.Job).HasForeignKey<BackgroundJob>(job => job.UploadSessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(job => job.UploadSessionId).IsUnique();
        builder.HasIndex(job => new { job.State, job.NextAttemptAt });
        builder.HasIndex(job => new { job.State, job.LeaseExpiresAt });
    }
}
