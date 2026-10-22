using NexusAuth.Shared.DependencyInjection;

namespace NexusAuth.Application.Clients.Authentication;

[KeyedService(typeof(IClientAuthenticationStrategy), OAuthClient.TokenEndpointAuthMethodClientSecretPost)]
internal class ClientSecretPostAuthenticationStrategy()
    : ClientSecretAuthenticationStrategy(
        OAuthClient.TokenEndpointAuthMethodClientSecretPost,
        "Client must authenticate with client_secret_post.",
        "AuthenticatedClientSecret");
