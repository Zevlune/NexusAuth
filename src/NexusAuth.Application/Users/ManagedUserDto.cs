namespace NexusAuth.Application.Users;

public sealed record ManagedUserDto(
    Guid Id, string Username, string Nickname, string? Email, string? PhoneNumber, bool IsActive,
    string? ExternalId, string? GivenName, string? FamilyName, string? Title, string? UserType,
    string? PreferredLanguage, string? Locale, string? Timezone, bool IsSystemAccount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
