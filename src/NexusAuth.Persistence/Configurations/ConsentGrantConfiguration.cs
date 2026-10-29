using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Persistence.Configurations;

public sealed class ConsentGrantConfiguration : IEntityTypeConfiguration<ConsentGrant>
{
    public void Configure(EntityTypeBuilder<ConsentGrant> builder)
    {
        builder.ToTable("consent_grants", table =>
        {
            table.HasCheckConstraint(
                "ck_consent_grants_scope_not_empty",
                "length(scope_normalized) > 0");
            table.HasCheckConstraint(
                "ck_consent_grants_claims_match_key",
                "claims_match_key = 'n' OR claims_match_key ~ '^v:[A-Za-z0-9_-]{43}$'");
            table.HasCheckConstraint(
                "ck_consent_grants_claims_key_consistency",
                "(claims IS NULL AND claims_match_key = 'n') "
                + "OR (claims IS NOT NULL AND claims_match_key LIKE 'v:%')");
        });

        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.Id).HasColumnName("id");

        builder.Property(grant => grant.UserId)
            .HasColumnName("user_id")
            .IsRequired();
        builder.Property(grant => grant.ClientId)
            .HasColumnName("client_id")
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(grant => grant.ScopeNormalized)
            .HasColumnName("scope_normalized")
            .HasMaxLength(512)
            .IsRequired();
        builder.Property(grant => grant.Claims)
            .HasColumnName("claims")
            .HasColumnType("text");
        builder.Property(grant => grant.ClaimsMatchKey)
            .HasColumnName("claims_match_key")
            .HasMaxLength(65)
            .IsRequired();
        builder.Property(grant => grant.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasIndex(grant => new
            {
                grant.UserId,
                grant.ClientId,
                grant.ScopeNormalized,
                grant.ClaimsMatchKey,
            })
            .HasDatabaseName("ix_consent_grants_identity")
            .IsUnique();
        builder.HasIndex(grant => new { grant.UserId, grant.ClientId })
            .HasDatabaseName("ix_consent_grants_user_client");
    }
}
