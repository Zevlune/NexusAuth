namespace NexusAuth.Application.Clients;

public record ClientCredentialDto(
    Guid Id,
    string Type,
    bool IsActive,
    DateTimeOffset CreatedAt);
