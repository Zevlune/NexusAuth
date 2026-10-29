using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Shared.OAuth;

namespace NexusAuth.Application.Clients.Authentication;

[KeyedService(typeof(IClientAuthenticationStrategy), OAuthClientAuthenticationMethods.ClientSecretBasic)]
internal class ClientSecretBasicAuthenticationStrategy()
    : ClientSecretAuthenticationStrategy(
        OAuthClientAuthenticationMethods.ClientSecretBasic,
        "Client must authenticate with client_secret_basic.",
        "AuthenticatedClientSecret");
