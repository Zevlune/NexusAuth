using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Host.Pages.Account;

[Authorize(AuthenticationSchemes = AppWebModule.AuthenticationScheme)]
public sealed class ConsentsModel(IConsentStore consents, IRefreshTokenRepository refreshTokens) : PageModel
{
    [BindProperty] public string ClientId { get; set; } = string.Empty;
    public IReadOnlyList<ConsentGrantState> Grants { get; private set; } = [];
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Grants = await consents.ListGrantsAsync(UserId, ct);
    }

    public async Task<IActionResult> OnPostRevokeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ClientId)) return BadRequest();
        await consents.RevokeGrantsAsync(UserId, ClientId, ct);
        await refreshTokens.RevokeAllForUserAndClientAsync(UserId, ClientId, ct);
        return RedirectToPage();
    }
}
