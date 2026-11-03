using NexusAuth.Application;
using NexusAuth.Application.Clients;
using NexusAuth.Domain.AggregateRoots.OAuthClients;

namespace NexusAuth.Application.Services.ApiResources;

public class ApiResourceService(
    IApiResourceRepository apiResourceRepository,
    IClientApiResourceRepository clientApiResourceRepository,
    IOAuthClientRepository clientRepository) : IApiResourceService
{
    public async Task<List<ApiResourceDto>> GetAllAsync(string? keyword = null, bool? isActive = null, CancellationToken ct = default)
    {
        var (resources, _) = await apiResourceRepository.GetPagedAsync(keyword, isActive, 1, int.MaxValue, ct);
        return [.. resources.Select(MapDto)];
    }

    public async Task<PagedResult<ApiResourceDto>> GetPagedAsync(
        string? keyword = null,
        bool? isActive = null,
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default)
    {
        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = NormalizePageSize(pageSize);
        var (resources, total) = await apiResourceRepository.GetPagedAsync(keyword, isActive, normalizedPage, normalizedPageSize, ct);
        return new PagedResult<ApiResourceDto>([.. resources.Select(MapDto)], total, normalizedPage, normalizedPageSize);
    }

    public async Task<ApiResourceDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var resource = await apiResourceRepository.FindByIdAsync(id, ct);
        return resource is null ? null : MapDto(resource);
    }

    /// <summary>
    /// </summary>
    
    public async Task<ApiResource> RegisterAsync(
        string name,
        string displayName,
        string audience,
        string? description = null,
        CancellationToken ct = default)
    {
        var existing = await apiResourceRepository.FindByNameAsync(name, ct);
        if (existing is not null)
            throw new ApiResourceAlreadyExistsException(name);

        var audienceOwner = await apiResourceRepository.FindByAudienceAsync(audience, ct);
        if (audienceOwner is not null)
            throw new ApiResourceAlreadyExistsException(audience);

        var resource = ApiResource.Create(name, displayName, audience, description);
        await apiResourceRepository.AddAsync(resource, ct);

        return resource;
    }

    public async Task<ApiResourceDto> CreateAsync(CreateApiResourceRequest request, CancellationToken ct = default)
    {
        var resource = await RegisterAsync(
            request.Name,
            request.DisplayName,
            request.Audience,
            request.Description,
            ct);

        return MapDto(resource);
    }

    public async Task<ApiResourceDto> UpdateAsync(Guid id, UpdateApiResourceRequest request, CancellationToken ct = default)
    {
        var resource = await apiResourceRepository.FindByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Api resource with id {id} not found.");
        EnsureNotSystemManaged(resource);

        if (request.Audience is not null
            && !string.Equals(request.Audience, resource.Audience, StringComparison.Ordinal))
        {
            throw new WorkbenchBusinessConflictException(
                $"API resource audience '{resource.Audience}' cannot be changed after creation.");
        }

        resource.Update(
            request.DisplayName,
            request.Audience,
            request.Description,
            request.IsActive);

        await apiResourceRepository.UpdateAsync(resource, ct);
        return MapDto(resource);
    }

    public async Task<ApiResourceDto> UpdateStatusAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        var resource = await apiResourceRepository.FindByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Api resource with id {id} not found.");
        EnsureNotSystemManaged(resource);

        resource.Update(isActive: isActive);
        await apiResourceRepository.UpdateAsync(resource, ct);
        return MapDto(resource);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var resource = await apiResourceRepository.FindByIdAsync(id, ct);
        if (resource is null)
            throw new KeyNotFoundException($"Api resource with id {id} not found.");
        EnsureNotSystemManaged(resource);

        await apiResourceRepository.DeleteAsync(resource, ct);
    }

    /// <summary>
    /// </summary>
    public async Task AssignToClientAsync(
        Guid clientId,
        Guid apiResourceId,
        CancellationToken ct = default)
    {
        var (client, resource) = await EnsureAssociationCanChangeAsync(clientId, apiResourceId, ct);
        var existing = await clientApiResourceRepository.GetResourcesByClientIdAsync(clientId, ct);
        if (existing.Any(associatedResource => associatedResource.Id == apiResourceId)
            && client.AllowedScopes.Contains(resource.Audience, StringComparer.Ordinal))
            return;

        var association = ClientApiResource.Create(clientId, apiResourceId);
        await clientApiResourceRepository.AddAsync(association, ct);
    }

    /// <summary>
    /// </summary>
    public async Task RevokeFromClientAsync(
        Guid clientId,
        Guid apiResourceId,
        CancellationToken ct = default)
    {
        var (client, resource) = await EnsureAssociationCanChangeAsync(clientId, apiResourceId, ct);
        var existing = await clientApiResourceRepository.GetResourcesByClientIdAsync(clientId, ct);
        if (!existing.Any(associatedResource => associatedResource.Id == apiResourceId)
            && !client.AllowedScopes.Contains(resource.Audience, StringComparer.Ordinal))
            return;

        await clientApiResourceRepository.RemoveAsync(clientId, apiResourceId, ct);
    }

    /// <summary>
    /// </summary>
    public async Task<IReadOnlyList<ApiResource>> GetClientResourcesAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
        return await clientApiResourceRepository.GetResourcesByClientIdAsync(clientId, ct);
    }

    /// <summary>
    /// </summary>
    public async Task<IReadOnlyList<ApiResource>> GetAllActiveResourcesAsync(
        CancellationToken ct = default)
    {
        return await apiResourceRepository.GetAllActiveAsync(ct);
    }

    private static ApiResourceDto MapDto(ApiResource resource)
    {
        return new ApiResourceDto(
            resource.Id,
            resource.Name,
            resource.DisplayName,
            resource.Audience,
            resource.Description,
            resource.IsActive,
            resource.CreatedAt,
            resource.IsSystemManaged);
    }

    private async Task<(OAuthClient Client, ApiResource Resource)> EnsureAssociationCanChangeAsync(
        Guid clientId,
        Guid apiResourceId,
        CancellationToken ct)
    {
        var resource = await apiResourceRepository.FindByIdAsync(apiResourceId, ct);
        if (resource is null)
            throw new KeyNotFoundException($"Api resource with id {apiResourceId} not found.");
        EnsureNotSystemManaged(resource);

        var client = await clientRepository.GetByIdAsync(clientId, ct);
        if (client is null)
            throw new KeyNotFoundException($"Client with id {clientId} not found.");
        if (client.IsSystemManaged)
            throw new WorkbenchBusinessConflictException($"Client '{client.ClientId}' is managed by Workbench configuration (由 Workbench 配置托管).");

        return (client, resource);
    }

    private static void EnsureNotSystemManaged(ApiResource resource)
    {
        if (resource.IsSystemManaged)
            throw new WorkbenchBusinessConflictException($"API resource '{resource.Name}' is managed by Workbench configuration (由 Workbench 配置托管).");
    }

    private static int NormalizePageSize(int pageSize)
    {
        return pageSize <= 0 ? 10 : Math.Min(pageSize, 100);
    }
}
