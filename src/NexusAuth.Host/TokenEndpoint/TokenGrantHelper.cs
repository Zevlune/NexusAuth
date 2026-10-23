using Microsoft.AspNetCore.Mvc;

namespace NexusAuth.Host.TokenEndpoint;

public static class TokenGrantHelper
{
    public static ClientAuthenticationInput? ParseClientAuthentication(TokenGrantContext context, string issuer)
    {
        var request = context.HttpContext.Request;
        var issuerBase = issuer.TrimEnd('/');
        var tokenEndpointUrl = issuerBase + "/connect/token";
        var input = context.Request;
        var parsed = OAuthClientAuthenticationParser.ResolveClientAuthentication(
            request.Headers.Authorization.ToString(), input.ClientId, input.ClientSecret,
            input.ClientAssertionType, input.ClientAssertion, tokenEndpointUrl,
            [tokenEndpointUrl, issuerBase]);
        return parsed.IsSuccess ? parsed.Authentication : null;
    }

    public static bool ShouldIssueRefreshToken(string scope) =>
        scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("offline_access", StringComparer.Ordinal);

    public static IActionResult InvalidClient(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"NexusAuth\"";
        return new UnauthorizedObjectResult(new { error = "invalid_client" });
    }

    public static IActionResult TokenIssuanceFailed()
    {
        return new BadRequestObjectResult(new
        {
            error = "invalid_grant",
            error_description = "Unable to issue tokens for this grant.",
        });
    }
}
