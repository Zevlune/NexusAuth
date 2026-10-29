namespace NexusAuth.Workbench.Api.Models.Requests;

public sealed record CreateOpenApiCredentialRequest(string Name, string TargetType, DateTimeOffset? ExpiresAt);
