namespace NexusAuth.Workbench.Api.Authentication.Tokens;

public interface IWorkbenchTokenClient
{
    Task<WorkbenchTokenResult> RefreshTokensAsync(string refreshToken, CancellationToken ct);
    Task<WorkbenchIntrospectionResult> IntrospectAccessTokenAsync(string accessToken, CancellationToken ct);
    Task RevokeTokenAsync(string token, string tokenTypeHint, CancellationToken ct);
}
