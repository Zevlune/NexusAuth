using Microsoft.AspNetCore.Mvc;
using NexusAuth.Application.Services.Scim;
using NexusAuth.Workbench.Api.Filters;
using NexusAuth.Workbench.Api.Models.Requests;

namespace NexusAuth.Workbench.Api.Controllers;

/// <summary>Internal-only management surface. Raw bearer tokens are returned once at creation.</summary>
[ApiController]
[WorkbenchApiResult]
[Route("api/scim-credentials")]
public sealed class ScimCredentialsController(IScimCredentialService credentialService) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ScimCredentialSummary>> GetAll(CancellationToken ct = default) => credentialService.GetAllAsync(ct);

    [HttpPost]
    public async Task<ScimCredentialCreated> Create([FromBody] CreateScimCredentialRequest request, CancellationToken ct = default)
    {
        return await credentialService.CreateAsync(request.Name, request.Scopes, request.ExpiresAt, ct);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateScimCredentialRequest request, CancellationToken ct = default)
    {
        var credential = await credentialService.UpdateAsync(id, request.Name, request.Scopes, request.ExpiresAt, request.IsActive, ct);
        return credential is null ? NotFound() : Ok(credential);
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct = default)
    {
        return await credentialService.RevokeAsync(id, ct) ? NoContent() : NotFound();
    }
}
