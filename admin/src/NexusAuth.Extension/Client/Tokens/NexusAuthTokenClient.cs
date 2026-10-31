using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace NexusAuth.Extension;

/// <summary>BFF 会话的附加令牌操作，复用标准 OIDC 方案的元数据缓存与后台 HttpClient。</summary>
public sealed class NexusAuthTokenClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> options,
    IOptions<NexusAuthClientOptions> clientOptions,
    NexusAuthClientSetup setup) : INexusAuthTokenClient
{
    private async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken ct)
    {
        var oidc = options.Get(setup.OpenIdConnectScheme);
        var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(ct).WaitAsync(ct);
        if (!string.Equals(configuration.Issuer, oidc.Authority, StringComparison.Ordinal))
            throw new InvalidOperationException("Discovery issuer must equal the configured Authority.");
        return configuration;
    }

    /// <inheritdoc />
    public async Task<NexusAuthTokenResult> RefreshTokensAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var configuration = await GetConfigurationAsync(ct);
        return await SendTokenRequestAsync(RequireEndpoint(configuration.TokenEndpoint, "token"),
            new Dictionary<string, string>
            {
                ["grant_type"] = OpenIdConnectGrantTypes.RefreshToken,
                ["refresh_token"] = refreshToken,
            }, ct);
    }

    /// <inheritdoc />
    public async Task<NexusAuthIntrospectionResult> IntrospectAccessTokenAsync(
        string accessToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var configuration = await GetConfigurationAsync(ct);
        var endpoint = RequireEndpoint(configuration.IntrospectionEndpoint, "introspection");
        using var response = await SendFormAsync(
            endpoint,
            new Dictionary<string, string> { ["token"] = accessToken },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw NexusAuthProtocolResponse.FromResponse(body, response.StatusCode);
        NexusAuthProtocolResponse.ThrowIfOAuthError(body, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(body);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object)
                throw NexusAuthProtocolResponse.InvalidResponse(response.StatusCode);
            var active = payload.TryGetProperty("active", out var activeProperty)
                && activeProperty.ValueKind == JsonValueKind.True;
            var subject = ReadOptionalString(payload, "sub");
            var clientId = ReadOptionalString(payload, "client_id");
            return new NexusAuthIntrospectionResult(active, subject, clientId);
        }
        catch (JsonException)
        {
            throw NexusAuthProtocolResponse.InvalidResponse(response.StatusCode);
        }
    }

    /// <inheritdoc />
    public async Task RevokeTokenAsync(string token, string tokenTypeHint, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var configuration = await GetConfigurationAsync(ct);
        var endpoint = RequireEndpoint(configuration.RevocationEndpoint, "revocation");
        var form = new Dictionary<string, string> { ["token"] = token };
        if (!string.IsNullOrEmpty(tokenTypeHint))
            form["token_type_hint"] = tokenTypeHint;

        using var response = await SendFormAsync(endpoint, form, ct);
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw NexusAuthProtocolResponse.FromResponse(body, response.StatusCode);
    }

    private async Task<NexusAuthTokenResult> SendTokenRequestAsync(
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        using var response = await SendFormAsync(endpoint, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw NexusAuthProtocolResponse.FromResponse(body, response.StatusCode);
        NexusAuthProtocolResponse.ThrowIfOAuthError(body, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(body);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object)
                throw NexusAuthProtocolResponse.InvalidResponse(response.StatusCode);
            var accessToken = ReadOptionalString(payload, "access_token");
            if (string.IsNullOrEmpty(accessToken))
                throw NexusAuthProtocolResponse.InvalidResponse(response.StatusCode);

            var expiresIn = 0;
            if (payload.TryGetProperty("expires_in", out var expiresInProperty))
            {
                if (expiresInProperty.ValueKind != JsonValueKind.Number
                    || !expiresInProperty.TryGetInt32(out expiresIn)
                    || expiresIn < 0)
                {
                    throw NexusAuthProtocolResponse.InvalidResponse(response.StatusCode);
                }
            }

            return new NexusAuthTokenResult(
                accessToken,
                ReadOptionalString(payload, "refresh_token"),
                ReadOptionalString(payload, "id_token"),
                expiresIn);
        }
        catch (JsonException)
        {
            throw NexusAuthProtocolResponse.InvalidResponse(response.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> SendFormAsync(
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        // 统一写入表单凭据；ClientSecretBasic 模式下由后台客户端的 handler 改写为 Basic 认证头。
        var settings = clientOptions.Value;
        form["client_id"] = settings.ClientId;
        form["client_secret"] = settings.ClientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var client = httpClientFactory.CreateClient(setup.BackchannelHttpClientName);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static string RequireEndpoint(string endpoint, string endpointName)
    {
        if (string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException(
                $"The provider does not publish a {endpointName} endpoint.");
        return endpoint;
    }

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        return payload.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
