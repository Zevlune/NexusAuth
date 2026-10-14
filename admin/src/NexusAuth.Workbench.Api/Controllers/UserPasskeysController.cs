using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Luck.AspNetCore.ApiResults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Workbench.Api.Controllers;

[Authorize]
[ApiController]
[ApiResultWrap]
[Route("api/users/{id:guid}/passkeys")]
public sealed class UserPasskeysController(IPasskeyManagementService passkeys, IUserRepository users) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!await IsAdministratorAsync(ct)) return Forbid();
        if (await users.FindByIdAsync(id, ct) is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Ok(await passkeys.GetAsync(id, ct));
    }

    [HttpPost("{credentialId:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, Guid credentialId, [FromBody] RevokePasskeyRequest request, CancellationToken ct)
    {
        if (!await IsAdministratorAsync(ct)) return Forbid();
        try
        {
            return await passkeys.RevokeAsync(id, credentialId, ActorId, request.Reason, ct)
                ? NoContent() : NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private Guid ActorId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
        ? id : Guid.Empty;

    // The project currently identifies its bootstrap administrator through
    // IsSystemAccount; a logged-in ordinary user is not an administrator.
    private async Task<bool> IsAdministratorAsync(CancellationToken ct) =>
        ActorId != Guid.Empty && await users.FindByIdAsync(ActorId, ct) is { IsActive: true, IsSystemAccount: true };
}

public sealed record RevokePasskeyRequest([Required, StringLength(500)] string Reason);
