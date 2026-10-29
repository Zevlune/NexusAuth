using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Workbench.Api.Authentication;

namespace NexusAuth.Workbench.Api.Authentication.Tokens;

/// <summary>Additional token operations needed by the BFF session, using the standard OIDC metadata.</summary>
public sealed class WorkbenchTokenClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> options) : IWorkbenchTokenClient
{
    public const string HttpClientName = "Workbench.Oidc";

    private async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken ct)
    {
        var oidc = options.Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme);
        var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(ct).WaitAsync(ct);
        if (!string.Equals(configuration.Issuer, oidc.Authority, StringComparison.Ordinal))
            throw new InvalidOperationException("Discovery issuer must equal the configured Authority.");
        return configuration;
    }

    public async Task<WorkbenchTokenResult> RefreshTokensAsync(string refreshToken, CancellationToken ct)
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
    public async Task<WorkbenchIntrospectionResult> IntrospectAccessTokenAsync(
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
            throw WorkbenchProtocolResponse.FromResponse(body, response.StatusCode);
        WorkbenchProtocolResponse.ThrowIfOAuthError(body, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(body);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object)
                throw WorkbenchProtocolResponse.InvalidResponse(response.StatusCode);
            var active = payload.TryGetProperty("active", out var activeProperty)
                && activeProperty.ValueKind == JsonValueKind.True;
            var subject = ReadOptionalString(payload, "sub");
            var clientId = ReadOptionalString(payload, "client_id");
            return new WorkbenchIntrospectionResult(active, subject, clientId);
        }
        catch (JsonException)
        {
            throw WorkbenchProtocolResponse.InvalidResponse(response.StatusCode);
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
        throw WorkbenchProtocolResponse.FromResponse(body, response.StatusCode);
    }

    private async Task<WorkbenchTokenResult> SendTokenRequestAsync(
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        using var response = await SendFormAsync(endpoint, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw WorkbenchProtocolResponse.FromResponse(body, response.StatusCode);
        WorkbenchProtocolResponse.ThrowIfOAuthError(body, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(body);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object)
                throw WorkbenchProtocolResponse.InvalidResponse(response.StatusCode);
            var accessToken = ReadOptionalString(payload, "access_token");
            if (string.IsNullOrEmpty(accessToken))
                throw WorkbenchProtocolResponse.InvalidResponse(response.StatusCode);

            var expiresIn = 0;
            if (payload.TryGetProperty("expires_in", out var expiresInProperty))
            {
                if (expiresInProperty.ValueKind != JsonValueKind.Number
                    || !expiresInProperty.TryGetInt32(out expiresIn)
                    || expiresIn < 0)
                {
                    throw WorkbenchProtocolResponse.InvalidResponse(response.StatusCode);
                }
            }

            return new WorkbenchTokenResult(
                accessToken,
                ReadOptionalString(payload, "refresh_token"),
                ReadOptionalString(payload, "id_token"),
                expiresIn);
        }
        catch (JsonException)
        {
            throw WorkbenchProtocolResponse.InvalidResponse(response.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> SendFormAsync(
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var client = httpClientFactory.CreateClient(HttpClientName);
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
