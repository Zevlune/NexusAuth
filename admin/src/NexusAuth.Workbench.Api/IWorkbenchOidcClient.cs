using System.Net;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace NexusAuth.Workbench.Api;

public interface IWorkbenchOidcClient
{
    Task<OpenIdConnectConfiguration> FetchConfigurationAsync(CancellationToken ct);
    Task<WorkbenchTokenResult> RefreshTokensAsync(string refreshToken, CancellationToken ct);
    Task<WorkbenchIntrospectionResult> IntrospectAccessTokenAsync(string accessToken, CancellationToken ct);
    Task RevokeTokenAsync(string token, string tokenTypeHint, CancellationToken ct);
}

public sealed record WorkbenchTokenResult(string AccessToken, string? RefreshToken, string? IdToken, int ExpiresIn);
public sealed record WorkbenchIntrospectionResult(bool Active, string? Subject, string? ClientId);

public sealed class WorkbenchProtocolException(string error, HttpStatusCode statusCode, Exception? innerException = null)
    : Exception($"The authorization server returned protocol error '{error}' (HTTP {(int)statusCode}).", innerException)
{
    public string Error { get; } = error;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
