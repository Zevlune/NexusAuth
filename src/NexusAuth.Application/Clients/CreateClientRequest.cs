namespace NexusAuth.Application.Clients;

public record CreateClientRequest(
    string ClientId,
    string ClientName,
    string? Description,
    List<string>? RedirectUris,
    List<string>? PostLogoutRedirectUris,
    List<string>? AllowedScopes,
    List<string>? AllowedGrantTypes,
    bool RequirePkce,
    string? TokenEndpointAuthMethod,
    bool? AutoGenerateJwks,
    string? Jwks,
    string? JwksUri,
    List<Guid>? ApiResourceIds = null);
