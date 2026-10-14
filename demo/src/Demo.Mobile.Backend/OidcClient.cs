using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;

namespace Demo.Mobile.Backend;

public sealed class OidcClient(HttpClient http, IConfiguration config)
{
    public string Authority => config["NexusAuth:Authority"]!.TrimEnd('/');
    public string ClientId => config["NexusAuth:ClientId"]!;
    public string RedirectUri => config["NexusAuth:RedirectUri"]!;
    public string Scope => config["NexusAuth:Scope"]!;
    public async Task<Discovery> Discover(CancellationToken ct)
    {
        var document = await http.GetFromJsonAsync<Discovery>($"{Authority}/.well-known/openid-configuration", ct)
            ?? throw new InvalidOperationException("Missing discovery document.");
        if (document.Issuer.TrimEnd('/') != Authority)
            throw new SecurityTokenValidationException("Unexpected issuer.");
        return document;
    }
    private HttpRequestMessage AuthenticatedPost(string endpoint, Dictionary<string, string> fields)
    {
        var secret = config["NexusAuth:ClientSecret"];
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Configure NexusAuth__ClientSecret on the backend.");
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(ClientId)}:{Uri.EscapeDataString(secret)}")));
        return request;
    }
    public async Task<TokenSet> Tokens(Discovery discovery, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var request = AuthenticatedPost(discovery.TokenEndpoint, fields);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized)
            throw new SessionRejectedException();
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<TokenSet>(ct)
            ?? throw new SecurityTokenValidationException("Missing tokens.");
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || tokens.ExpiresIn <= 0 || tokens.TokenType != "Bearer")
            throw new SecurityTokenValidationException("Invalid token response.");
        return tokens;
    }
    public async Task<UserInfo> ValidateLogin(Discovery discovery, TokenSet tokens, string nonce, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tokens.IdToken)) throw new SecurityTokenValidationException("Missing ID token.");
        var jwks = await http.GetStringAsync(discovery.JwksUri, ct);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(tokens.IdToken, new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = Authority,
            ValidateAudience = true, ValidAudience = ClientId,
            ValidateIssuerSigningKey = true, IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
            ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30)
        }, out _);
        if (principal.FindFirst("nonce")?.Value != nonce) throw new SecurityTokenValidationException("Invalid nonce.");
        var user = await User(discovery, tokens.AccessToken, ct);
        if (string.IsNullOrEmpty(user.Sub) || user.Sub != principal.FindFirst("sub")?.Value)
            throw new SecurityTokenValidationException("Userinfo subject mismatch.");
        return user;
    }
    public async Task<UserInfo> User(Discovery discovery, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, discovery.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new SessionRejectedException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserInfo>(ct) ?? throw new SecurityTokenValidationException("Missing userinfo.");
    }
    public async Task<bool> Revoke(Discovery discovery, string token, string hint, CancellationToken ct)
    {
        using var request = AuthenticatedPost(discovery.RevocationEndpoint, new() { ["token"] = token, ["token_type_hint"] = hint });
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }
}
public sealed class SessionRejectedException : Exception;
public sealed record Discovery(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("userinfo_endpoint")] string UserInfoEndpoint,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("revocation_endpoint")] string RevocationEndpoint);
public sealed record TokenSet(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string TokenType)
{
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
}
public sealed record UserInfo(
    [property: JsonPropertyName("sub")] string Sub,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("preferred_username")] string? PreferredUsername,
    [property: JsonPropertyName("email")] string? Email);
