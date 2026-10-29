using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Shared.OAuth;

namespace NexusAuth.Application.Clients.Authentication;

[KeyedService(typeof(IClientAuthenticationStrategy), OAuthClientAuthenticationMethods.ClientSecretPost)]
internal class ClientSecretPostAuthenticationStrategy()
    : ClientSecretAuthenticationStrategy(
        OAuthClientAuthenticationMethods.ClientSecretPost,
        "Client must authenticate with client_secret_post.",
        "AuthenticatedClientSecret");
