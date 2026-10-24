using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NexusAuth.Workbench.Api.IntegrationTests;

// Test-only controllers prove policy selection through endpoint routing and authorization middleware.
[ApiController]
public sealed class TestWorkbenchAccessController : ControllerBase
{
    [HttpGet("/__test/access/fallback")]
    public IActionResult Fallback() => Ok();

    [HttpGet("/__test/access/default")]
    [Authorize]
    public IActionResult Default() => Ok();

    [HttpGet("/__test/access/public")]
    [AllowAnonymous]
    public IActionResult Public() => Ok();

    [HttpGet("/__test/access/named")]
    [Authorize(Policy = "test-named")]
    public IActionResult Named() => Ok();
}
