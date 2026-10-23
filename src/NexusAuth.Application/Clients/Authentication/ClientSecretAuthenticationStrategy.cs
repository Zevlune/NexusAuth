using NexusAuth.Application.Clients;

namespace NexusAuth.Application.Clients.Authentication;

internal abstract class ClientSecretAuthenticationStrategy(
    string tokenEndpointAuthMethod,
    string mismatchError,
    string successAuditReason) : IClientAuthenticationStrategy
{
    protected string TokenEndpointAuthMethod { get; } = tokenEndpointAuthMethod;

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

        if (!string.Equals(input.TokenEndpointAuthMethod, TokenEndpointAuthMethod, StringComparison.Ordinal))
        {
            return Task.FromResult(ClientAuthenticationStrategyResult.Failure(
                "WrongAuthMethod",
                mismatchError));
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

        return Task.FromResult(ClientAuthenticationStrategyResult.Success(successAuditReason));
    }
}
