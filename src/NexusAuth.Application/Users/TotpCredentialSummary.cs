namespace NexusAuth.Application.Users;

public sealed record TotpCredentialSummary(Guid Id, string DisplayName, bool IsEnabled, DateTimeOffset CreatedAt);
