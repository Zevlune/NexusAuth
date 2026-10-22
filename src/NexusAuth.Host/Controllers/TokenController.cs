using Microsoft.AspNetCore.Mvc;
using NexusAuth.Host.TokenEndpoint;
using NexusAuth.Shared.DependencyInjection;

namespace NexusAuth.Host.Controllers;

[ApiController]
public class TokenController(IServiceProvider serviceProvider) : ControllerBase
{
    /// <summary>Receives form data and dispatches token grants through DI strategies.</summary>
    [HttpPost("/connect/token")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token(
        [FromForm(Name = "grant_type")] string? grantType,
        [FromForm(Name = "client_id")] string? clientId = null,
        [FromForm(Name = "client_secret")] string? clientSecret = null,
        [FromForm] string? code = null,
        [FromForm(Name = "redirect_uri")] string? redirectUri = null,
        [FromForm(Name = "code_verifier")] string? codeVerifier = null,
        [FromForm] string? scope = null,
        [FromForm(Name = "refresh_token")] string? refreshToken = null,
        [FromForm(Name = "device_code")] string? deviceCode = null,
        [FromForm(Name = "client_assertion_type")] string? clientAssertionType = null,
        [FromForm(Name = "client_assertion")] string? clientAssertion = null,
        [FromForm] string? nickname = null,
        CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        if (string.IsNullOrWhiteSpace(grantType))
            return BadRequest(new { error = "invalid_request", error_description = "grant_type is required." });

        var strategy = serviceProvider.ResolveKeyed<ITokenGrantStrategy>(grantType)
            ?? serviceProvider.GetRequiredKeyedService<ITokenGrantStrategy>(TokenGrantKeys.Unsupported);
        return await strategy.ExecuteAsync(new TokenGrantContext(HttpContext, new TokenRequest(
            grantType, clientId, clientSecret, code, redirectUri, codeVerifier, scope,
            refreshToken, deviceCode, clientAssertionType, clientAssertion, nickname)), ct);
    }
}
