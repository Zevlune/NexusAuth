using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Domain.Repositories;
using System.Text;
using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Shared.OAuth;
using static NexusAuth.Host.TokenEndpoint.TokenGrantHelper;

namespace NexusAuth.Host.TokenEndpoint;

[KeyedService(typeof(ITokenGrantStrategy), OAuthGrantTypes.WeChatMiniProgram)]
public class WeChatMiniProgramGrantStrategy(
    IClientService clientService,
    ITokenService tokenService,
    ISecurityPolicyService securityPolicyService,
    IWeChatMiniProgramLoginService weChatMiniProgramLoginService,
    IOptions<WeChatMiniProgramOptions> weChatOptions,
    IUserRepository userRepository,
    IOptions<JwtOptions> jwtOptions)
    : ITokenGrantStrategy
{
    private readonly WeChatMiniProgramOptions _weChatOptions = weChatOptions.Value;

    public async Task<IActionResult> ExecuteAsync(TokenGrantContext context, CancellationToken ct)
    {
        // Public clients must not submit another client authentication method.
        if (context.Request.ClientSecret is not null
            || context.Request.ClientAssertionType is not null
            || context.Request.ClientAssertion is not null
            || !string.IsNullOrWhiteSpace(context.HttpContext.Request.Headers.Authorization))
            return InvalidClient(context.HttpContext);

        // MVC converts whitespace to null; preserve it for nickname validation.
        var clientId = context.Request.ClientId;
        var code = context.Request.Code;
        var scope = context.Request.Scope;
        var nickname = context.Request.Nickname ?? context.HttpContext.Request.Form["nickname"].FirstOrDefault();
        if (!_weChatOptions.Enabled)
            return new BadRequestObjectResult(new { error = "unsupported_grant_type", error_description = "WeChat mini program login is disabled." });

        if (string.IsNullOrWhiteSpace(clientId)
            || !string.Equals(clientId, _weChatOptions.ClientId, StringComparison.Ordinal))
        {
            return InvalidClient(context.HttpContext);
        }

        var clientAuthentication = await clientService.AuthenticateClientAsync(
            new ClientAuthenticationInput(clientId, null, null, null),
            requireClientAuthentication: false,
            ct);
        if (!clientAuthentication.IsSuccess || clientAuthentication.Client is null)
            return InvalidClient(context.HttpContext);

        if (!string.Equals(
                clientAuthentication.Client.TokenEndpointAuthMethod,
                OAuthClientAuthenticationMethods.None,
                StringComparison.Ordinal))
            return InvalidClient(context.HttpContext);

        if (!securityPolicyService.CheckClient(clientId).IsSuccess)
            return new BadRequestObjectResult(new { error = "unauthorized_client", error_description = "Client is blocked by security policy." });

        if (!clientAuthentication.Client.IsGrantTypeAllowed(OAuthGrantTypes.WeChatMiniProgram))
        {
            return new BadRequestObjectResult(new
            {
                error = "unauthorized_client",
                error_description = "Client is not allowed to use the WeChat mini program grant type.",
            });
        }

        if (string.IsNullOrWhiteSpace(code))
            return new BadRequestObjectResult(new { error = "invalid_request", error_description = "code is required." });

        if (string.IsNullOrWhiteSpace(scope))
            return new BadRequestObjectResult(new { error = "invalid_request", error_description = "scope is required." });

        var scopeValidation = await clientService.ValidateScopesAsync(clientId, scope, allowIdentityScopes: true, ct);
        if (!scopeValidation.IsSuccess)
        {
            return new BadRequestObjectResult(new
            {
                error = scopeValidation.ErrorCode ?? "invalid_scope",
                error_description = scopeValidation.Error,
            });
        }

        if (scopeValidation.NormalizedScope!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(OidcScopes.OfflineAccess, StringComparer.Ordinal)
            && !clientAuthentication.Client.IsGrantTypeAllowed(OAuthGrantTypes.RefreshToken))
        {
            return new BadRequestObjectResult(new
            {
                error = "unauthorized_client",
                error_description = "Client is not allowed to use refresh_token.",
            });
        }

        if (nickname is not null)
        {
            nickname = nickname.Trim();
            if (nickname.Length < 1 || nickname.EnumerateRunes().Count() > 100)
                return new BadRequestObjectResult(new { error = "invalid_request", error_description = "Nickname must contain between 1 and 100 characters." });

            if (!scopeValidation.NormalizedScope!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(OidcScopes.Profile, StringComparer.Ordinal))
                return new BadRequestObjectResult(new { error = "invalid_scope", error_description = "The profile scope is required to set a nickname." });
        }

        WeChatMiniProgramLoginResult login;
        try
        {
            login = await weChatMiniProgramLoginService.AuthenticateAsync(code, ct);
        }
        catch (WeChatMiniProgramLoginException exception)
        {
            return new ObjectResult(new { error = exception.ErrorCode, error_description = exception.Message })
            {
                StatusCode = exception.ErrorCode == "temporarily_unavailable" ? 503 : 400,
            };
        }

        var userPolicy = securityPolicyService.CheckUser(login.User);
        if (!userPolicy.IsSuccess || !login.User.IsActive)
            return new BadRequestObjectResult(new { error = "invalid_grant", error_description = "The user account is unavailable." });

        if (nickname is not null && login.User.IsSystemAccount)
            return new BadRequestObjectResult(new { error = "invalid_grant", error_description = "System account profiles cannot be modified." });

        if (nickname is not null && !string.Equals(login.User.Nickname, nickname, StringComparison.Ordinal))
        {
            login.User.UpdateNickname(nickname);
            await userRepository.UpdateAsync(login.User, ct);
        }

        TokenIssueResult accessTokenResult;
        string? refreshToken;
        string? idToken = null;
        try
        {
            accessTokenResult = await tokenService.IssueAccessTokenWithMetadataAsync(
                clientId,
                scopeValidation.NormalizedScope!,
                null,
                login.User.Id,
                null,
                ct);
            refreshToken = ShouldIssueRefreshToken(scopeValidation.NormalizedScope!)
                ? await tokenService.IssueRefreshTokenAsync(clientId, login.User.Id, scopeValidation.NormalizedScope!, ct)
                : null;
            var includeIdToken = scopeValidation.NormalizedScope!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(OidcScopes.OpenId, StringComparer.Ordinal);
            if (includeIdToken)
            {
                idToken = await tokenService.IssueIdTokenAsync(
                    clientId,
                    login.User.Id,
                    null,
                    accessTokenResult.AccessToken,
                    authenticatedAt: login.AuthenticatedAt,
                    acr: "urn:nexusauth:acr:external",
                    amr: "wechat",
                    ct: ct);
            }
        }
        catch (InvalidOperationException)
        {
            return TokenIssuanceFailed();
        }

        return new OkObjectResult(new
        {
            access_token = accessTokenResult.AccessToken,
            token_type = "Bearer",
            refresh_token = refreshToken,
            scope = scopeValidation.NormalizedScope,
            id_token = idToken,
            expires_in = checked(jwtOptions.Value.AccessTokenLifetimeMinutes * 60),
            user = new
            {
                sub = login.User.Id.ToString(),
                name = login.User.Nickname,
            },
        });
    }
}
