using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Persistence.Configurations;

public class ClientApiResourceConfiguration : IEntityTypeConfiguration<ClientApiResource>
{
    public void Configure(EntityTypeBuilder<ClientApiResource> builder)
    {
        builder.ToTable("client_api_resources");

        builder.HasKey(x => new { x.ClientId, x.ApiResourceId });

        builder.Property(x => x.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(x => x.ApiResourceId)
            .HasColumnName("api_resource_id")
            .IsRequired();

        builder.HasOne<OAuthClient>()
            .WithMany()
            .HasForeignKey(x => x.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ApiResource>()
            .WithMany()
            .HasForeignKey(x => x.ApiResourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
