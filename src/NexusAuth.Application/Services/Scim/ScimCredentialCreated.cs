namespace NexusAuth.Application.Services.Scim;

public sealed record ScimCredentialCreated(ScimCredentialSummary Credential, string Token);
