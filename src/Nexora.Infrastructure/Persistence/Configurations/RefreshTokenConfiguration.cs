using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexora.Domain.Authentication;

namespace Nexora.Infrastructure.Persistence.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens", table =>
        {
            table.HasCheckConstraint("CK_RefreshTokens_HashLength", "octet_length(\"TokenHash\") = 32");
            table.HasCheckConstraint("CK_RefreshTokens_Expiration", "\"ExpiresAt\" > \"CreatedAt\"");
            table.HasCheckConstraint("CK_RefreshTokens_Consumption", "(\"UsedAt\" IS NULL) = (\"SuccessorId\" IS NULL)");
        });
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();
        builder.Property(token => token.TokenHash).HasColumnType("bytea").HasMaxLength(32);
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasAlternateKey(token => new { token.SessionId, token.Id });
        builder.HasOne(token => token.Session).WithMany(session => session.RefreshTokens)
            .HasForeignKey(token => token.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(token => token.Successor).WithMany()
            .HasForeignKey(token => new { token.SessionId, token.SuccessorId })
            .HasPrincipalKey(token => new { token.SessionId, token.Id }).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(token => token.SuccessorId).IsUnique();
    }
}
