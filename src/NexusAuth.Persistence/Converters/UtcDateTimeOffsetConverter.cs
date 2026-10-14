using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NexusAuth.Persistence.Converters;

/// <summary>
/// Keeps persisted instants and mapped query parameters compatible with PostgreSQL timestamptz.
/// EF applies the same converter to nullable DateTimeOffset properties and preserves nulls.
/// </summary>
public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value.ToUniversalTime());
