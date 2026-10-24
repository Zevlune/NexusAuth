using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace NexusAuth.Workbench.Api.Controllers;

[ApiController]
public sealed class CurrentIdentityController(
    IAuthorizationService authorization,
    IOptions<AuthorizationOptions> authorizationOptions) : ControllerBase
{
    [HttpGet("/api/auth/me")]
    [AllowAnonymous]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { isAuthenticated = false, isAdministrator = false });

        var policy = authorizationOptions.Value.DefaultPolicy;
        var access = await authorization.AuthorizeAsync(User, HttpContext, policy);
        var isAdministrator = access.Succeeded;

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
