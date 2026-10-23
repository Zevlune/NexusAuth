using NexusAuth.Shared.DependencyInjection;

namespace NexusAuth.Application.Clients.Authentication;

[KeyedService(typeof(IClientAuthenticationStrategy), OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt)]
internal class PrivateKeyJwtAuthenticationStrategy(
    ITokenBlacklistRepository tokenBlacklistRepository)
    : ClientJwtAuthenticationStrategy(tokenBlacklistRepository)
{
    protected override string MissingAudienceError =>
        "assertion audience is required for private_key_jwt validation.";

    protected override string SuccessAuditReason => "AuthenticatedPrivateKeyJwt";

    protected override ClientAssertionValidationResult ValidateAssertion(
        string assertionJwt,
        OAuthClient client,
        IReadOnlyCollection<string> expectedAudiences)
    {
        return ClientPrivateKeyJwtValidator.Validate(assertionJwt, client, expectedAudiences);
    }
}
