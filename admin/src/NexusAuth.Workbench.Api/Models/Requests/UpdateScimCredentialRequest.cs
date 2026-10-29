namespace NexusAuth.Workbench.Api.Models.Requests;

public sealed record UpdateScimCredentialRequest(string Name, IReadOnlyCollection<string>? Scopes, DateTimeOffset? ExpiresAt, bool IsActive);
