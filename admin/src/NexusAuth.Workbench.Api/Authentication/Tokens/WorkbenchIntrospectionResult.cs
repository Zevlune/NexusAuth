namespace NexusAuth.Workbench.Api.Authentication.Tokens;

public sealed record WorkbenchIntrospectionResult(bool Active, string? Subject, string? ClientId);
