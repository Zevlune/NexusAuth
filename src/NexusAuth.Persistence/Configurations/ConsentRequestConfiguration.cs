using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Persistence.Configurations;

public sealed class ConsentRequestConfiguration : IEntityTypeConfiguration<ConsentRequest>
{
    public void Configure(EntityTypeBuilder<ConsentRequest> builder)
    {
        builder.ToTable("consent_requests", table =>
        {
            table.HasCheckConstraint(
                "ck_consent_requests_request_id_hash_base64url",
                "request_id_hash ~ '^[A-Za-z0-9_-]{43}$'");
            table.HasCheckConstraint(
                "ck_consent_requests_expiry_after_creation",
                "expires_at > created_at");
            table.HasCheckConstraint(
                "ck_consent_requests_status",
                "status IN ('Pending', 'Approved', 'Consumed')");
            table.HasCheckConstraint(
                "ck_consent_requests_state_timestamps",
                "(status = 'Pending' AND approved_at IS NULL AND consumed_at IS NULL) "
                + "OR (status = 'Approved' AND approved_at IS NOT NULL AND consumed_at IS NULL) "
                + "OR (status = 'Consumed' AND approved_at IS NOT NULL AND consumed_at IS NOT NULL)");
        });

        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id).HasColumnName("id");

        builder.Property(request => request.RequestIdHash)
            .HasColumnName("request_id_hash")
            .HasMaxLength(43)
            .IsRequired();
        builder.Property(request => request.UserId)
            .HasColumnName("user_id")
            .IsRequired();
        builder.Property(request => request.Query)
            .HasColumnName("query")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(request => request.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(request => request.Remember)
            .HasColumnName("remember")
            .IsRequired();
        builder.Property(request => request.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();
        builder.Property(request => request.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();
        builder.Property(request => request.ApprovedAt)
            .HasColumnName("approved_at");
        builder.Property(request => request.ConsumedAt)
            .HasColumnName("consumed_at");

        builder.HasIndex(request => request.RequestIdHash)
            .HasDatabaseName("ix_consent_requests_request_id_hash")
            .IsUnique();
        builder.HasIndex(request => new
            {
                request.RequestIdHash,
                request.UserId,
                request.Status,
                request.ExpiresAt,
            })
            .HasDatabaseName("ix_consent_requests_consume");
        builder.HasIndex(request => new { request.UserId, request.Status, request.ExpiresAt })
            .HasDatabaseName("ix_consent_requests_user_state");
        builder.HasIndex(request => request.ExpiresAt)
            .HasDatabaseName("ix_consent_requests_expires_at");
    }
}
