namespace NexusAuth.Application.Services.ApiResources;

public record UpdateApiResourceRequest(
    string? DisplayName,
    string? Audience,
    string? Description,
    bool? IsActive);
