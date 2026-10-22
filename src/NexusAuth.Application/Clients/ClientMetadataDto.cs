namespace NexusAuth.Application.Clients;

public record ClientMetadataDto(
    List<ClientOptionDto> Scopes,
    List<ClientOptionDto> GrantTypes,
    List<ClientOptionDto> TokenEndpointAuthMethods);
