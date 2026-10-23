using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Shared.DependencyInjection;
using static NexusAuth.Host.TokenEndpoint.TokenGrantHelper;

namespace NexusAuth.Host.TokenEndpoint;

[KeyedService(typeof(ITokenGrantStrategy), "client_credentials")]
public class ClientCredentialsGrantStrategy(
    IClientService clientService,
    ITokenService tokenService,
    ISecurityPolicyService securityPolicyService,
    IOptions<JwtOptions> jwtOptions)
    : ITokenGrantStrategy
{
    public async Task<IActionResult> ExecuteAsync(TokenGrantContext context, CancellationToken ct)
    {
        var authentication = ParseClientAuthentication(context, jwtOptions.Value.Issuer);
        if (authentication is null)
            return InvalidClient(context.HttpContext);

        var scope = context.Request.Scope;
        if (string.IsNullOrWhiteSpace(authentication.ClientId))
            return InvalidClient(context.HttpContext);

        var clientAuthentication = await clientService.AuthenticateClientAsync(authentication, requireClientAuthentication: true, ct);
        if (!clientAuthentication.IsSuccess || clientAuthentication.Client is null)
            return InvalidClient(context.HttpContext);

        if (string.IsNullOrWhiteSpace(scope))
            return new BadRequestObjectResult(new { error = "invalid_request", error_description = "scope is required." });

        var policy = securityPolicyService.CheckClient(clientAuthentication.Client.ClientId);
        if (!policy.IsSuccess)
            return new BadRequestObjectResult(new { error = "access_denied", error_description = policy.Error });

        if (!clientAuthentication.Client.IsGrantTypeAllowed("client_credentials"))
        {
            return new BadRequestObjectResult(new
            {
                error = "unauthorized_client",
                error_description = "Client is not allowed to use client_credentials grant type.",
            });
        }

        var scopeValidation = await clientService.ValidateScopesAsync(
            clientAuthentication.Client.ClientId,
            scope,
            allowIdentityScopes: false,
            ct);

        if (!scopeValidation.IsSuccess)
            return new BadRequestObjectResult(new { error = scopeValidation.ErrorCode ?? "invalid_scope", error_description = scopeValidation.Error });

        string accessToken;
        try
        {
            accessToken = await tokenService.IssueAccessTokenAsync(
                clientAuthentication.Client.ClientId,
                scopeValidation.NormalizedScope!,
                null,
                null,
                null,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return new BadRequestObjectResult(new { error = "invalid_scope", error_description = ex.Message });
        }

        return new OkObjectResult(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            scope = scopeValidation.NormalizedScope,
            expires_in = checked(jwtOptions.Value.AccessTokenLifetimeMinutes * 60),
        });
    }
}
