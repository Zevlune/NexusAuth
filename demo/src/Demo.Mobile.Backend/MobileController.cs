using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace Demo.Mobile.Backend;

[ApiController]
public sealed partial class MobileController(MobileStore store, OidcClient oidc, IConfiguration config) : ControllerBase
{
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
    [GeneratedRegex("^[A-Za-z0-9._~-]{43,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex VerifierPattern();
    private static bool ValidToken(string? value) => value is not null && TokenPattern().IsMatch(value);
    private IActionResult Unavailable() => StatusCode(502, new { error = "provider_unavailable" });

    [HttpPost("/api/mobile/login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        if (!ValidToken(request.Challenge) || !ValidToken(request.State))
            return BadRequest(new { error = "invalid_request" });
        try
        {
            var discovery = await oidc.Discover(ct);
            var verifier = MobileStore.RandomToken();
            var nonce = MobileStore.RandomToken();
            var state = store.AddFlow(new(verifier, nonce, request.State, request.Challenge));
            var url = QueryHelpers.AddQueryString(discovery.AuthorizationEndpoint, new Dictionary<string, string?>
            {
                ["response_type"] = "code", ["client_id"] = oidc.ClientId, ["redirect_uri"] = oidc.RedirectUri,
                ["scope"] = oidc.Scope, ["state"] = state, ["nonce"] = nonce,
                ["code_challenge"] = MobileStore.Challenge(verifier), ["code_challenge_method"] = "S256"
            });
            return Ok(new { authorizationUrl = url });
        }
        catch (Exception ex) when (IsProviderFailure(ex)) { return Unavailable(); }
    }

    [HttpGet("/signin-oidc")]
    public async Task<IActionResult> Callback(string? state, string? code, string? error, CancellationToken ct)
    {
        if (!ValidToken(state)) return BadRequest(new { error = "invalid_state" });
        var flow = store.ConsumeFlow(state!);
        if (flow is null) return BadRequest(new { error = "invalid_state" });
        if (error is not null || string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            return AppError(flow.AppState);
        try
        {
            var discovery = await oidc.Discover(ct);
            var tokens = await oidc.Tokens(discovery, new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = oidc.RedirectUri, ["code_verifier"] = flow.Verifier
            }, ct);
            var user = await oidc.ValidateLogin(discovery, tokens, flow.Nonce, ct);
            var ticket = store.AddTicket(new(flow.AppChallenge, new(tokens, user)));
            return Redirect(QueryHelpers.AddQueryString(config["Mobile:CallbackUri"]!, new Dictionary<string, string?>
            { ["ticket"] = ticket, ["state"] = flow.AppState }));
        }
        catch (Exception ex) when (IsProviderFailure(ex) || ex is SessionRejectedException)
        { return AppError(flow.AppState); }
    }
    private IActionResult AppError(string state) => Redirect(QueryHelpers.AddQueryString(config["Mobile:CallbackUri"]!,
        new Dictionary<string, string?> { ["error"] = "login_failed", ["state"] = state }));

    [HttpPost("/api/mobile/exchange")]
    [EnableRateLimiting("login")]
    public IActionResult Exchange(ExchangeRequest request)
    {
        if (!ValidToken(request.Ticket) || request.Verifier is null || !VerifierPattern().IsMatch(request.Verifier))
            return BadRequest(new { error = "invalid_request" });
        var session = store.Exchange(request.Ticket, request.Verifier, out var token);
        if (session is null) return BadRequest(new { error = "invalid_ticket" });
        return Ok(new { sessionToken = token, expiresAt = session.ExpiresAt, user = session.User });
    }

    [HttpGet("/api/mobile/profile")]
    public Task<IActionResult> Profile(CancellationToken ct) => WithSession(false, ct);
    [HttpPost("/api/mobile/refresh")]
    public Task<IActionResult> Refresh(CancellationToken ct) => WithSession(true, ct);

    private (string Token, MobileSession Session)? Session()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[7..];
        if (!ValidToken(token)) return null;
        var session = store.FindSession(token);
        return session is null ? null : (token, session);
    }
    private async Task<IActionResult> WithSession(bool forceRefresh, CancellationToken ct)
    {
        var entry = Session();
        if (entry is null) return Unauthorized(new { error = "session_expired" });
        var (token, session) = entry.Value;
        await session.Gate.WaitAsync(ct);
        try
        {
            if (session.Closed || session.ExpiresAt <= DateTimeOffset.UtcNow)
                return Unauthorized(new { error = "session_expired" });
            var discovery = await oidc.Discover(ct);
            var refreshed = false;
            if (forceRefresh || session.Tokens.IssuedAt.AddSeconds(session.Tokens.ExpiresIn - 30) <= DateTimeOffset.UtcNow)
            {
                if (string.IsNullOrEmpty(session.Tokens.RefreshToken)) throw new SessionRejectedException();
                session.Tokens = await oidc.Tokens(discovery, new()
                { ["grant_type"] = "refresh_token", ["refresh_token"] = session.Tokens.RefreshToken }, ct);
                refreshed = true;
            }
            var user = await oidc.User(discovery, session.Tokens.AccessToken, ct);
            if (user.Sub != session.User.Sub) throw new SessionRejectedException();
            session.User = user;
            return Ok(new { user, expiresAt = session.ExpiresAt, refreshed });
        }
        catch (SessionRejectedException)
        {
            session.Closed = true;
            store.RemoveSession(token);
            return Unauthorized(new { error = "session_expired" });
        }
        catch (Exception ex) when (IsProviderFailure(ex)) { return Unavailable(); }
        finally { session.Gate.Release(); }
    }

    [HttpPost("/api/mobile/logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var entry = Session();
        if (entry is null) return Unauthorized(new { error = "session_expired" });
        var (token, session) = entry.Value;
        await session.Gate.WaitAsync(ct);
        try
        {
            session.Closed = true;
            store.RemoveSession(token); // 先使 App 会话失效，SSO 暂时不可用也不能恢复本地会话。
            var discovery = await oidc.Discover(ct);
            var revoked = true;
            if (!string.IsNullOrEmpty(session.Tokens.RefreshToken))
                revoked &= await oidc.Revoke(discovery, session.Tokens.RefreshToken, "refresh_token", ct);
            revoked &= await oidc.Revoke(discovery, session.Tokens.AccessToken, "access_token", ct);
            return Ok(new { revoked });
        }
        catch (Exception ex) when (IsProviderFailure(ex)) { return Ok(new { revoked = false }); }
        finally { session.Gate.Release(); }
    }
    private static bool IsProviderFailure(Exception ex) => ex is HttpRequestException or TaskCanceledException
        or SecurityTokenException or System.Text.Json.JsonException or InvalidOperationException;
}
public sealed record LoginRequest(string Challenge, string State);
public sealed record ExchangeRequest(string Ticket, string Verifier);
