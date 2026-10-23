namespace NexusAuth.Application.Clients;

public record GenerateClientCredentialRequest(
    string? TokenEndpointAuthMethod = null,
    bool? AutoGenerateJwks = null,
    string? Description = null);
