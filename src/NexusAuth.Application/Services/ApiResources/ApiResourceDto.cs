namespace NexusAuth.Application.Services.ApiResources;

public record ApiResourceDto(
    Guid Id,
    string Name,
    string DisplayName,
    string Audience,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAt);
