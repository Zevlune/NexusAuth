namespace NexusAuth.Workbench.Api.Authentication.Tokens;

public sealed record WorkbenchTokenResult(string AccessToken, string? RefreshToken, string? IdToken, int ExpiresIn);
