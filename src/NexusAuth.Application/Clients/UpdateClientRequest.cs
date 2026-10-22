namespace NexusAuth.Application.Clients;

public record UpdateClientRequest(
    string? ClientName,
    string? Description,
    List<string>? RedirectUris,
    List<string>? PostLogoutRedirectUris,
    List<string>? AllowedScopes,
    List<string>? AllowedGrantTypes,
    bool? RequirePkce,
    string? TokenEndpointAuthMethod,
    string? Jwks,
    string? JwksUri,
    bool? IsActive,
    List<Guid>? ApiResourceIds = null);
