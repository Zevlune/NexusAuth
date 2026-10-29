using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.Entities;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

public sealed class RefreshTokenClaimsMappingTests
{
    [Fact]
    public void Claims_json_is_an_optional_postgres_text_column()
    {
        using var context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=metadata-only;Username=test;Password=test")
            .Options);
        var entityType = context.Model.FindEntityType(typeof(RefreshToken));
        var property = entityType?.FindProperty(nameof(RefreshToken.ClaimsJson));

        Assert.NotNull(property);
        Assert.Equal("claims_json", property.GetColumnName());
        Assert.Equal("text", property.GetColumnType());
        Assert.True(property.IsNullable);
    }
}
