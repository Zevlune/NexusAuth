using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NexusAuth.Application.Services;
using NexusAuth.Application.Clients;
using NexusAuth.Domain.Repositories;
using NexusAuth.Host.Controllers;

namespace NexusAuth.Host.Pages;

public class ConsentModel(IConsentStore consentStore, IAuthorizationService authorizationService, IClientService clientService, IOptions<JwtOptions> jwtOptions) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "id")]
    public string Id { get; set; } = string.Empty;

    [BindProperty]
    public bool Remember { get; set; }

    public string ClientId { get; private set; } = string.Empty;
    public string RedirectUri { get; private set; } = string.Empty;
    public IReadOnlyList<string> ScopeItems { get; private set; } = [];
    public IReadOnlyList<string> ClaimItems { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var request = await LoadRequestAsync(ct);
        if (request is null)
            return InvalidRequest();
        Populate(request.Query);
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(CancellationToken ct)
    {
        var userId = GetUserId();
        var request = await LoadRequestAsync(ct);
        if (request is null || userId is null || !await consentStore.ApproveRequestAsync(Id, userId.Value, Remember, ct))
            return InvalidRequest();

        var url = QueryHelpers.AddQueryString("/connect/authorize" + request.Query, AuthorizeController.ConsentParameterName, Id);
        return LocalRedirect(url);
    }

    public async Task<IActionResult> OnPostDenyAsync(CancellationToken ct)
    {
        var userId = GetUserId();
        var request = await LoadRequestAsync(ct);
        if (request is null || userId is null || !await consentStore.ApproveRequestAsync(Id, userId.Value, false, ct))
            return InvalidRequest();
        var consumed = await consentStore.ConsumeApprovedRequestAsync(Id, userId.Value, ct);
        if (consumed is null)
            return InvalidRequest();

        var query = QueryHelpers.ParseQuery(consumed.Query);
        var redirectUri = GetValue(query, "redirect_uri");
        if (!(await clientService.ValidateClientRedirectUriAsync(GetValue(query, "client_id"), redirectUri, ct)).IsSuccess)
            return InvalidRequest();
        var parameters = new Dictionary<string, string?>
        {
            ["error"] = "access_denied",
            ["error_description"] = "The resource owner denied the request.",
            ["state"] = GetValue(query, "state") is { Length: > 0 } state ? state : null,
            ["iss"] = jwtOptions.Value.Issuer.TrimEnd('/'),
        };
        var present = parameters.Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (!string.Equals(GetValue(query, "response_mode"), "form_post", StringComparison.Ordinal))
            return Redirect(QueryHelpers.AddQueryString(redirectUri, present));

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var inputs = string.Join("", present.Select(pair =>
            $"<input type=\"hidden\" name=\"{System.Net.WebUtility.HtmlEncode(pair.Key)}\" value=\"{System.Net.WebUtility.HtmlEncode(pair.Value)}\" />"));
        return Content($"<!doctype html><html><body><form method=\"post\" action=\"{System.Net.WebUtility.HtmlEncode(redirectUri)}\">{inputs}<noscript><button type=\"submit\">Continue</button></noscript></form><script>document.forms[0].submit();</script></body></html>", "text/html; charset=utf-8");
    }

    private Guid? GetUserId() => User.Identity?.IsAuthenticated == true
        && Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private async Task<ConsentRequestState?> LoadRequestAsync(CancellationToken ct)
    {
        var userId = GetUserId();
        return userId is null || string.IsNullOrWhiteSpace(Id)
            ? null
            : await consentStore.GetRequestAsync(Id, userId.Value, ct);
    }

    private IActionResult InvalidRequest() => BadRequest("The consent request is invalid or expired.");

    private void Populate(string originalQuery)
    {
        var query = QueryHelpers.ParseQuery(originalQuery);
        ClientId = GetValue(query, "client_id");
        RedirectUri = GetValue(query, "redirect_uri");
        ScopeItems = GetValue(query, "scope").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var requested = authorizationService.ParseRequestedClaims(GetValue(query, "claims"));
        ClaimItems = requested.IdTokenClaimRequests.Concat(requested.UserInfoClaimRequests)
            .Select(pair => FormatClaim(pair.Key, pair.Value)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static string GetValue(Dictionary<string, StringValues> query, string name) =>
        query.TryGetValue(name, out var value) ? value.ToString() : string.Empty;

    private static string FormatClaim(string name, OidcClaimRequest request)
    {
        var details = new List<string>();
        if (request.Essential == true) details.Add("essential");
        if (!string.IsNullOrWhiteSpace(request.Value)) details.Add($"value={request.Value}");
        if (request.Values.Count > 0) details.Add($"values={string.Join('|', request.Values)}");
        return details.Count == 0 ? name : $"{name} ({string.Join(", ", details)})";
    }
}
