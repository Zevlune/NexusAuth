namespace NexusAuth.Host.TokenEndpoint;

public record TokenRequest(
    string? GrantType,
    string? ClientId = null,
    string? ClientSecret = null,
    string? Code = null,
    string? RedirectUri = null,
    string? CodeVerifier = null,
    string? Scope = null,
    string? RefreshToken = null,
    string? DeviceCode = null,
    string? ClientAssertionType = null,
    string? ClientAssertion = null,
    string? Nickname = null);
