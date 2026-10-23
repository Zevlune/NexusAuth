using Microsoft.AspNetCore.Mvc;
using NexusAuth.Application.Users;

namespace NexusAuth.Workbench.Api.Controllers;

[ApiController]
[Route("api/users")]
public sealed class UsersController(IUserManagementService userManagementService) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> GetPaged([FromQuery] string? keyword, [FromQuery] bool? isActive, [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
        => ExecuteAsync(async () => Ok(await userManagementService.GetPagedAsync(keyword, isActive, page, pageSize, ct)), ct);

    [HttpGet("{id:guid}")]
    public Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
        => ExecuteAsync(async () => await userManagementService.GetByIdAsync(id, ct) is { } user ? Ok(user) : NotFound(), ct);

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateManagedUserRequest request, CancellationToken ct = default)
        => ExecuteAsync(async () => Ok(await userManagementService.CreateAsync(request, ct)), ct);

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateManagedUserRequest request, CancellationToken ct = default)
        => ExecuteAsync(async () => Ok(await userManagementService.UpdateProfileAsync(id, request, ct)), ct);

    [HttpPatch("{id:guid}/status")]
    public Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateManagedUserStatusRequest request, CancellationToken ct = default)
        => ExecuteAsync(async () => Ok(await userManagementService.UpdateStatusAsync(id, request.IsActive, ct)), ct);

    [HttpPost("{id:guid}/reset-password")]
    public Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetManagedUserPasswordRequest request, CancellationToken ct = default)
        => ExecuteAsync(async () => { await userManagementService.ResetPasswordAsync(id, request, ct); return NoContent(); }, ct);

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
        => ExecuteAsync(async () => { await userManagementService.DeleteAsync(id, ct); return NoContent(); }, ct);

    [HttpPost("{id:guid}/revoke-tokens")]
    public Task<IActionResult> RevokeTokens(Guid id, CancellationToken ct = default)
        => ExecuteAsync(async () => { await userManagementService.RevokeTokensAsync(id, ct); return NoContent(); }, ct);

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (KeyNotFoundException exception) { return NotFound(new { message = exception.Message }); }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
        catch (InvalidOperationException exception) { return Conflict(new { message = exception.Message }); }
    }
}

public sealed record UpdateManagedUserStatusRequest(bool IsActive);
