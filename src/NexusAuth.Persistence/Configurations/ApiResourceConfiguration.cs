using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusAuth.Domain.AggregateRoots.ApiResources;

namespace NexusAuth.Persistence.Configurations;

public class ApiResourceConfiguration : IEntityTypeConfiguration<ApiResource>
{
    public void Configure(EntityTypeBuilder<ApiResource> builder)
    {
        builder.ToTable("api_resources");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");

        builder.Property(r => r.Name)
            .HasColumnName("name")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(r => r.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(r => r.Audience)
            .HasColumnName("audience")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(r => r.Description)
            .HasColumnName("description")
            .HasColumnType("text");

        builder.Property(r => r.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(r => r.IsSystemManaged)
            .HasColumnName("is_system_managed")
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // PostgreSQL's system xmin prevents a stale resource snapshot from
        // restoring an old audience or IsSystemManaged value.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion();

        builder.HasIndex(r => r.Name).IsUnique();
        builder.HasIndex(r => r.Audience).IsUnique();
    }
}
