using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Luck.AspNetCore.ApiResults;
using Microsoft.AspNetCore.Mvc;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Workbench.Api.Controllers;

[ApiController]
[ApiResultWrap]
[Route("api/users/{id:guid}/passkeys")]
public sealed class UserPasskeysController(IPasskeyManagementService passkeys, IUserRepository users) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (await users.FindByIdAsync(id, ct) is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Ok(await passkeys.GetAsync(id, ct));
    }

    [HttpPost("{credentialId:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, Guid credentialId, [FromBody] RevokePasskeyRequest request, CancellationToken ct)
    {
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

}

public sealed record RevokePasskeyRequest([Required, StringLength(500)] string Reason);
