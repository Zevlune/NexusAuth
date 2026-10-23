namespace NexusAuth.Application.Clients;

public record ClientAuthenticationInput(
    string? ClientId,
    string? ClientSecret,
    string? ClientAssertionType,
    string? ClientAssertion,
    string? AssertionAudience = null,
    string? TokenEndpointAuthMethod = null);
