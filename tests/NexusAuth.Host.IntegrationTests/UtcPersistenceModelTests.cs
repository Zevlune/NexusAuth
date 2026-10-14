using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.RegularExpressions;
using NexusAuth.Domain.Entities;
using NexusAuth.Persistence.Converters;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

public sealed class UtcPersistenceModelTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(-5)]
    [InlineData(0)]
    public void Every_timestamp_property_converts_to_utc_without_changing_its_instant(int offsetHours)
    {
        using var context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=metadata-only;Username=test;Password=test")
            .Options);
        var timestamp = new DateTimeOffset(2030, 1, 2, 12, 34, 56, TimeSpan.FromHours(offsetHours));
        var properties = context.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
            .Where(property => (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTimeOffset))
            .ToArray();

        Assert.NotEmpty(properties);
        Assert.Contains(properties, property => property.ClrType == typeof(DateTimeOffset?));
        Assert.Contains(properties, property => property.ClrType == typeof(DateTimeOffset));
        foreach (var property in properties)
        {
            var converter = Assert.IsType<UtcDateTimeOffsetConverter>(property.GetValueConverter());
            Assert.Equal("timestamp with time zone", property.GetColumnType());
            var providerValue = Assert.IsType<DateTimeOffset>(converter.ConvertToProvider(timestamp));
            Assert.Equal(TimeSpan.Zero, providerValue.Offset);
            Assert.Equal(timestamp.UtcTicks, providerValue.UtcTicks);
            var modelValue = Assert.IsType<DateTimeOffset>(converter.ConvertFromProvider(timestamp));
            Assert.Equal(TimeSpan.Zero, modelValue.Offset);
            Assert.Equal(timestamp.UtcTicks, modelValue.UtcTicks);
            Assert.Null(converter.ConvertToProvider(null));
            Assert.Null(converter.ConvertFromProvider(null));
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(-5)]
    public void Non_utc_expiration_query_parameters_are_translated_as_utc(int offsetHours)
    {
        using var context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=metadata-only;Username=test;Password=test")
            .Options);
        var timestamp = new DateTimeOffset(2030, 1, 2, 12, 34, 56, TimeSpan.FromHours(offsetHours));

        var sql = context.Set<ScimServicePrincipalCredential>()
            .Where(credential => credential.ExpiresAt > timestamp).ToQueryString();

        var parameter = Regex.Match(sql, "^-- @\\w+='([^']+)'");
        Assert.True(parameter.Success, sql);
        var converted = DateTimeOffset.Parse(parameter.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, converted.Offset);
        Assert.Equal(timestamp.UtcTicks, converted.UtcTicks);
    }
}
