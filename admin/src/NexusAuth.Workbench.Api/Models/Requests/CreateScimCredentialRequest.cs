namespace NexusAuth.Workbench.Api.Models.Requests;

public sealed record CreateScimCredentialRequest(string Name, IReadOnlyCollection<string>? Scopes, DateTimeOffset? ExpiresAt);
