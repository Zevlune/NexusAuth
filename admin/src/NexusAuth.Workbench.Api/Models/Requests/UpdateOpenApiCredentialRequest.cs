namespace NexusAuth.Workbench.Api.Models.Requests;

public sealed record UpdateOpenApiCredentialRequest(string Name, DateTimeOffset? ExpiresAt, bool IsActive);
