using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Persistence.Configurations;

public sealed class SecondFactorRecoveryAuditConfiguration : IEntityTypeConfiguration<SecondFactorRecoveryAudit>
{
    public void Configure(EntityTypeBuilder<SecondFactorRecoveryAudit> builder)
    {
        builder.ToTable("second_factor_recovery_audits");
        builder.HasKey(audit => audit.Id);
        builder.Property(audit => audit.Id).HasColumnName("id");
        builder.Property(audit => audit.ActorId).HasColumnName("actor_id").IsRequired();
        builder.Property(audit => audit.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(audit => audit.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
        builder.Property(audit => audit.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.HasIndex(audit => audit.UserId)
            .HasDatabaseName("ix_second_factor_recovery_audits_user_id");
        builder.HasIndex(audit => audit.OccurredAt)
            .HasDatabaseName("ix_second_factor_recovery_audits_occurred_at");
    }
}
