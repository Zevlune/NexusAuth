using NexusAuth.Application.Clients;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Shared.OAuth;

namespace NexusAuth.Host.TokenEndpoint;

[KeyedService(typeof(IRefreshTokenClientStrategy), RefreshTokenClientKeys.Confidential)]
public class ConfidentialRefreshTokenClientStrategy(IClientService clientService) : IRefreshTokenClientStrategy
{
    public Task<ClientAuthenticationResult> AuthenticateAsync(TokenGrantContext context, ClientAuthenticationInput input, CancellationToken ct) =>
        clientService.AuthenticateClientAsync(input, requireClientAuthentication: true, ct);

    public bool IsGrantAllowed(OAuthClient? client) =>
        client?.IsGrantTypeAllowed(OAuthGrantTypes.RefreshToken) == true;
}
