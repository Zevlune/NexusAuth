namespace NexusAuth.Application.Clients;

public record GeneratedClientCredentialDto(
    string Type,
    string? ClientSecret,
    string? PrivateKeyPem,
    string? Jwks,
    string? Description);
