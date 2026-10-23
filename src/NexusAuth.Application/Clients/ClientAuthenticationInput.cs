namespace NexusAuth.Application.Clients;

public record ClientAuthenticationInput(
    string? ClientId,
    string? ClientSecret,
    string? ClientAssertionType,
    string? ClientAssertion,
    string? AssertionAudience = null,
    string? TokenEndpointAuthMethod = null,
    IReadOnlyList<string>? AssertionAudiences = null)
{
    public IReadOnlyList<string> GetAcceptedAssertionAudiences()
    {
        if (AssertionAudiences is { Count: > 0 })
            return [.. AssertionAudiences.Where(audience => !string.IsNullOrWhiteSpace(audience)).Distinct(StringComparer.Ordinal)];

        return string.IsNullOrWhiteSpace(AssertionAudience) ? [] : [AssertionAudience];
    }
}
