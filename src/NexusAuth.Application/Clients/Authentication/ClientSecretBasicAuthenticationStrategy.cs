using NexusAuth.Shared.DependencyInjection;

namespace NexusAuth.Application.Clients.Authentication;

[KeyedService(typeof(IClientAuthenticationStrategy), OAuthClient.TokenEndpointAuthMethodClientSecretBasic)]
internal class ClientSecretBasicAuthenticationStrategy()
    : ClientSecretAuthenticationStrategy(
        OAuthClient.TokenEndpointAuthMethodClientSecretBasic,
        "Client must authenticate with client_secret_basic.",
        "AuthenticatedClientSecret");
