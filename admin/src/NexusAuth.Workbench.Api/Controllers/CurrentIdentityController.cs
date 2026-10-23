using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Workbench.Api.Controllers;

[ApiController]
public sealed class CurrentIdentityController(IUserRepository users) : ControllerBase
{
    [HttpGet("/api/auth/me")]
    [AllowAnonymous]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { isAuthenticated = false, isAdministrator = false });

        var isAdministrator = await WorkbenchAdministratorAuthorizationHandler.IsAdministratorAsync(
            User,
            users,
            HttpContext,
            ct);

        return Ok(new
        {
            isAuthenticated = true,
            isAdministrator,
            user = new
            {
                id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),
                name = User.Identity.Name
            }
        });
    }
}
