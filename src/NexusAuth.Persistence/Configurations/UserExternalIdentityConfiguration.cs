using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Persistence.Configurations;

public sealed class UserExternalIdentityConfiguration : IEntityTypeConfiguration<UserExternalIdentity>
{
    public void Configure(EntityTypeBuilder<UserExternalIdentity> builder)
    {
        builder.ToTable("user_external_identities");
        builder.HasKey(identity => identity.Id);
        builder.Property(identity => identity.Id).HasColumnName("id");
        builder.Property(identity => identity.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(identity => identity.Provider).HasColumnName("provider").HasMaxLength(32).IsRequired();
        builder.Property(identity => identity.Issuer).HasColumnName("issuer").HasMaxLength(256).IsRequired();
        builder.Property(identity => identity.Subject).HasColumnName("subject").HasMaxLength(256).IsRequired();
        builder.Property(identity => identity.UnionId).HasColumnName("union_id").HasMaxLength(256);
        builder.Property(identity => identity.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(identity => identity.LastAuthenticatedAt).HasColumnName("last_authenticated_at").IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(identity => new { identity.Provider, identity.Issuer, identity.Subject })
            .IsUnique()
            .HasDatabaseName("ux_user_external_identities_provider_issuer_subject");
        builder.HasIndex(identity => new { identity.Provider, identity.Issuer, identity.UnionId })
            .HasFilter("union_id IS NOT NULL")
            .HasDatabaseName("ix_user_external_identities_provider_issuer_union_id");
        builder.HasIndex(identity => identity.UserId)
            .HasDatabaseName("ix_user_external_identities_user_id");

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_user_external_identities_authentication_after_creation",
            "last_authenticated_at >= created_at"));
    }
}
