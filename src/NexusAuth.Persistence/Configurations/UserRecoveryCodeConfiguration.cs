using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Persistence.Configurations;

public sealed class UserRecoveryCodeConfiguration : IEntityTypeConfiguration<UserRecoveryCode>
{
    public void Configure(EntityTypeBuilder<UserRecoveryCode> builder)
    {
        builder.ToTable("user_recovery_codes");
        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).HasColumnName("id");
        builder.Property(code => code.UserId).HasColumnName("user_id").IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(code => code.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(code => code.CodeHash).HasColumnName("code_hash").HasMaxLength(64).IsRequired();
        builder.Property(code => code.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(code => code.UsedAt).HasColumnName("used_at");
        builder.HasIndex(code => new { code.UserId, code.CodeHash })
            .IsUnique()
            .HasDatabaseName("ux_user_recovery_codes_user_hash");
        builder.HasIndex(code => new { code.UserId, code.UsedAt })
            .HasDatabaseName("ix_user_recovery_codes_user_used");
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_user_recovery_codes_hash_hex",
            "code_hash ~ '^[A-Fa-f0-9]{64}$'"));
    }
}
