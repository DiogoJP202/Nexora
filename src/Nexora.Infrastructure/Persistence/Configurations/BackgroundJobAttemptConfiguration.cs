using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Jobs;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class BackgroundJobAttemptConfiguration : IEntityTypeConfiguration<BackgroundJobAttempt>
{
    public void Configure(EntityTypeBuilder<BackgroundJobAttempt> builder)
    {
        builder.ToTable("BackgroundJobAttempts");
        builder.HasKey(attempt => attempt.Id);
        builder.Property(attempt => attempt.Id).ValueGeneratedNever();
        builder.HasOne(attempt => attempt.Job).WithMany().HasForeignKey(attempt => attempt.JobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(attempt => attempt.JobId);
    }
}
