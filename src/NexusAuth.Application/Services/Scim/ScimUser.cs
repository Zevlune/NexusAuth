namespace NexusAuth.Application.Services.Scim;

public sealed record ScimUser(
    Guid Id, string UserName, string? ExternalId, bool Active, string? Email, string? PhoneNumber,
    string? GivenName, string? FamilyName, string? MiddleName, string? HonorificPrefix, string? HonorificSuffix,
    string? ProfileUrl, string? Title, string? UserType, string? PreferredLanguage, string? Locale,
    string? Timezone, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
