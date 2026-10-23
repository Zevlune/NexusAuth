using NexusAuth.Shared.DependencyInjection;

namespace NexusAuth.Application.Clients.Authentication;

[KeyedService(typeof(IClientAuthenticationStrategy), OAuthClient.TokenEndpointAuthMethodClientSecretJwt)]
internal class ClientSecretJwtAuthenticationStrategy(
    ITokenBlacklistRepository tokenBlacklistRepository)
    : ClientJwtAuthenticationStrategy(tokenBlacklistRepository)
{
    protected override string MissingAudienceError =>
        "assertion audience is required for client_secret_jwt validation.";

    protected override string SuccessAuditReason => "AuthenticatedClientSecretJwt";

    protected override ClientAssertionValidationResult ValidateAssertion(
        string assertionJwt,
        OAuthClient client,
        string expectedAudience)
    {
        return ClientSecretJwtValidator.Validate(assertionJwt, client, expectedAudience);
    }
}
