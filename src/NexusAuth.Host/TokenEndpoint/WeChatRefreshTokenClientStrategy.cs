using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Shared.OAuth;

namespace NexusAuth.Host.TokenEndpoint;

[KeyedService(typeof(IRefreshTokenClientStrategy), RefreshTokenClientKeys.WeChatMiniProgram)]
public class WeChatRefreshTokenClientStrategy(IClientService clientService) : IRefreshTokenClientStrategy
{
    public async Task<ClientAuthenticationResult> AuthenticateAsync(TokenGrantContext context, ClientAuthenticationInput input, CancellationToken ct)
    {
        var result = await clientService.AuthenticateClientAsync(input, requireClientAuthentication: false, ct);
        if (!result.IsSuccess)
            return result;

        if (result.Client?.TokenEndpointAuthMethod != OAuthClientAuthenticationMethods.None
            || input.ClientSecret is not null || input.ClientAssertion is not null
            || input.ClientAssertionType is not null || !string.IsNullOrWhiteSpace(context.HttpContext.Request.Headers.Authorization))
            return ClientAuthenticationResult.Failure("invalid_client", "Public clients must not submit client credentials.");

        return result;
    }

    public bool IsGrantAllowed(OAuthClient? client) =>
        client?.IsGrantTypeAllowed(OAuthGrantTypes.RefreshToken) == true
        && client.IsGrantTypeAllowed(OAuthGrantTypes.WeChatMiniProgram);
}
