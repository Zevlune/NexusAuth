using NexusAuth.Application.Clients;
using NexusAuth.Shared.OAuth;

namespace NexusAuth.Application.Clients.Authentication;

internal abstract class ClientJwtAuthenticationStrategy(
    ITokenBlacklistRepository tokenBlacklistRepository) : IClientAuthenticationStrategy
{
    protected abstract string MissingAudienceError { get; }

    protected abstract string SuccessAuditReason { get; }

    public async Task<ClientAuthenticationStrategyResult> AuthenticateAsync(
        ClientAuthenticationInput input,
        OAuthClient client,
        CancellationToken ct = default)
    {
        if (!string.Equals(input.ClientAssertionType, OAuthClientAssertionTypes.JwtBearer, StringComparison.Ordinal))
            return ClientAuthenticationStrategyResult.Failure(
                "WrongAuthMethod",
                "client_assertion_type is invalid for this client.");

        if (string.IsNullOrWhiteSpace(input.ClientAssertion))
            return ClientAuthenticationStrategyResult.Failure(
                "MissingCredential",
                "client_assertion is required.");

        var acceptedAudiences = input.GetAcceptedAssertionAudiences();
        if (acceptedAudiences.Count == 0)
            return ClientAuthenticationStrategyResult.Failure("MissingAssertionAudience", MissingAudienceError);

        var assertionValidation = ValidateAssertion(input.ClientAssertion, client, acceptedAudiences);
        if (!assertionValidation.IsSuccess)
        {
            return ClientAuthenticationStrategyResult.Failure(
                "InvalidAssertion",
                assertionValidation.Error ?? "Invalid client assertion.");
        }

        if (string.IsNullOrWhiteSpace(assertionValidation.Jti) || assertionValidation.ExpiresAt is null)
        {
            return ClientAuthenticationStrategyResult.Failure(
                "IncompleteAssertion",
                "client_assertion metadata is incomplete.");
        }

        var replayKey = BuildClientAssertionReplayKey(client.ClientId, assertionValidation.Jti);
        if (await tokenBlacklistRepository.ExistsActiveAsync(replayKey, DateTimeOffset.UtcNow, ct))
        {
            return ClientAuthenticationStrategyResult.Failure(
                "AssertionReplay",
                "client_assertion has already been used.");
        }

        await tokenBlacklistRepository.AddAsync(
            TokenBlacklistEntry.Create(replayKey, "client_assertion", client.ClientId, assertionValidation.ExpiresAt.Value),
            ct);

        return ClientAuthenticationStrategyResult.Success(SuccessAuditReason);
    }

    protected abstract ClientAssertionValidationResult ValidateAssertion(
        string assertionJwt,
        OAuthClient client,
        IReadOnlyCollection<string> expectedAudiences);

    private static string BuildClientAssertionReplayKey(string clientId, string assertionJti)
    {
        return $"client_assertion:{clientId}:{assertionJti}";
    }
}
