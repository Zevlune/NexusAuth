using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusAuth.Domain.AggregateRoots.Users;

namespace NexusAuth.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id");

        builder.Property(u => u.Username)
            .HasColumnName("username")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(u => u.ExternalId)
            .HasColumnName("external_id")
            .HasMaxLength(256);

        builder.Property(u => u.PasswordHash)
            .HasColumnName("password_hash")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(u => u.Email)
            .HasColumnName("email")
            .HasMaxLength(256);

        builder.Property(u => u.PhoneNumber)
            .HasColumnName("phone_number")
            .HasMaxLength(20);

        builder.Property(u => u.Nickname)
            .HasColumnName("nickname")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(u => u.GivenName).HasColumnName("given_name").HasMaxLength(100);
        builder.Property(u => u.FamilyName).HasColumnName("family_name").HasMaxLength(100);
        builder.Property(u => u.MiddleName).HasColumnName("middle_name").HasMaxLength(100);
        builder.Property(u => u.HonorificPrefix).HasColumnName("honorific_prefix").HasMaxLength(50);
        builder.Property(u => u.HonorificSuffix).HasColumnName("honorific_suffix").HasMaxLength(50);
        builder.Property(u => u.ProfileUrl).HasColumnName("profile_url").HasMaxLength(2048);
        builder.Property(u => u.Title).HasColumnName("title").HasMaxLength(256);
        builder.Property(u => u.UserType).HasColumnName("user_type").HasMaxLength(100);
        builder.Property(u => u.PreferredLanguage).HasColumnName("preferred_language").HasMaxLength(35);
        builder.Property(u => u.Locale).HasColumnName("locale").HasMaxLength(35);
        builder.Property(u => u.Timezone).HasColumnName("timezone").HasMaxLength(100);

        builder.Property(u => u.Gender)
            .HasColumnName("gender")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(u => u.Ethnicity)
            .HasColumnName("ethnicity")
            .HasMaxLength(50);

        // 中文注释：EF 模型中刻意不配置 HasDefaultValue。配置后 EF 会按 sentinel 判断“未设置”并在 INSERT 时省略该列，
        // 结果取决于 EF 版本约定以及 SQL 默认值是否与模型一致（sentinel 为 false 时，is_active DEFAULT true 会覆盖显式的 false）。
        // 领域对象总会给这些字段赋值，INSERT 始终显式写入；数据库默认值只保留在 production-init.sql 中。
        builder.Property(u => u.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(u => u.IsSystemAccount)
            .HasColumnName("is_system_account")
            .IsRequired();

        builder.Property(u => u.FailedLoginAttempts)
            .HasColumnName("failed_login_attempts")
            .IsRequired();

        builder.Property(u => u.LockedUntil)
            .HasColumnName("locked_until");

        builder.Property(u => u.TokenInvalidBefore)
            .HasColumnName("token_invalid_before");

        builder.Property(u => u.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(u => u.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(u => u.Username).IsUnique();
        builder.HasIndex(u => u.ExternalId).IsUnique().HasFilter("external_id IS NOT NULL");
        builder.HasIndex(u => u.Email).IsUnique().HasFilter("email IS NOT NULL");
        builder.HasIndex(u => u.PhoneNumber).IsUnique().HasFilter("phone_number IS NOT NULL");
    }
}
