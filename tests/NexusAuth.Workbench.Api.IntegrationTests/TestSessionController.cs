using NexusAuth.Workbench.Api.Authentication;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusAuth.Workbench.Api;

namespace NexusAuth.Workbench.Api.IntegrationTests;

[ApiController]
public sealed class TestSessionController : ControllerBase
{
    [HttpGet("/__test/sign-in/{userId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> SignIn(Guid userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, "Test Cookie User"),
            ],
            WorkbenchAuthenticationDefaults.CookieScheme));
        var properties = new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = $"cookie-access:{userId}" },
            new AuthenticationToken { Name = "refresh_token", Value = "cookie-refresh" },
            new AuthenticationToken { Name = "id_token", Value = "cookie-id" },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture),
            },
        ]);

        await HttpContext.SignInAsync(
            WorkbenchAuthenticationDefaults.CookieScheme,
            principal,
            properties);
        return Ok();
    }
}
