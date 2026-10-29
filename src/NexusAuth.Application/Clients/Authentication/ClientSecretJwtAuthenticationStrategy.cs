using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Shared.OAuth;

namespace NexusAuth.Application.Clients.Authentication;

[KeyedService(typeof(IClientAuthenticationStrategy), OAuthClientAuthenticationMethods.ClientSecretJwt)]
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
        IReadOnlyCollection<string> expectedAudiences)
    {
        return ClientSecretJwtValidator.Validate(assertionJwt, client, expectedAudiences);
    }
}
