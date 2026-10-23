using NexusAuth.Application.Clients;
using NexusAuth.Shared.DependencyInjection;

namespace NexusAuth.Application.Clients.Authentication;

[KeyedService(typeof(IClientAuthenticationStrategy), OAuthClient.TokenEndpointAuthMethodNone)]
internal class NoneAuthenticationStrategy : IClientAuthenticationStrategy
{
    public Task<ClientAuthenticationStrategyResult> AuthenticateAsync(
        ClientAuthenticationInput input,
        OAuthClient client,
        CancellationToken ct = default)
    {
        var hasAssertion = !string.IsNullOrWhiteSpace(input.ClientAssertion)
            || !string.IsNullOrWhiteSpace(input.ClientAssertionType);
        if (hasAssertion)
        {
            return Task.FromResult(ClientAuthenticationStrategyResult.Failure(
                "WrongAuthMethod",
                "This client requires client secret authentication."));
        }

        if (string.IsNullOrWhiteSpace(input.ClientSecret))
        {
            return Task.FromResult(ClientAuthenticationStrategyResult.Failure(
                "MissingCredential",
                "client_secret is required."));
        }

        if (!client.VerifyClientSecret(input.ClientSecret))
        {
            return Task.FromResult(ClientAuthenticationStrategyResult.Failure(
                "InvalidCredential",
                "Invalid client secret."));
        }

        return Task.FromResult(ClientAuthenticationStrategyResult.Success("AuthenticatedClientSecret"));
    }
}
