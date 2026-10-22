namespace NexusAuth.Application.Services.ApiResources;

public record CreateApiResourceRequest(
    string Name,
    string DisplayName,
    string Audience,
    string? Description);
