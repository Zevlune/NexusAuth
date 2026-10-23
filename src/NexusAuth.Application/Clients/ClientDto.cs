namespace NexusAuth.Application.Clients;

public record ClientDto(
    Guid Id,
    string ClientId,
    List<ClientCredentialDto> Credentials,
    string TokenEndpointAuthMethod,
    string? Jwks,
    string? JwksUri,
    string ClientName,
    string? Description,
    List<string> RedirectUris,
    List<string> PostLogoutRedirectUris,
    List<string> AllowedScopes,
    List<string> AllowedGrantTypes,
    bool RequirePkce,
    bool IsActive,
    DateTimeOffset CreatedAt)
{
    public List<Guid> ApiResourceIds { get; init; } = [];
}
