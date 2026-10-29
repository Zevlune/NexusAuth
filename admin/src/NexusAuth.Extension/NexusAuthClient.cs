using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace NexusAuth.Extension;

/// <summary>Implements OAuth and OpenID Connect backchannel requests with HttpClient.</summary>
public sealed class NexusAuthClient(
    IHttpClientFactory httpClientFactory,
    IOptions<NexusAuthClientOptions> options,
    IConfigurationManager<OpenIdConnectConfiguration> configurationManager) : INexusAuthClient
{
    /// <summary>The named HttpClient used for discovery, JWKS, and OAuth requests.</summary>
    public const string HttpClientName = "NexusAuth.Backchannel";

    private readonly NexusAuthClientOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<OpenIdConnectConfiguration> FetchConfigurationAsync(CancellationToken ct)
    {
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await configurationManager.GetConfigurationAsync(ct).WaitAsync(ct);
        }
        catch (InvalidOperationException exception)
        {
            var protocolException = FindProtocolException(exception);
            if (protocolException is not null)
                throw protocolException;
            throw;
        }

        var error = NexusAuthConfigurationValidation.ValidateConfiguration(configuration, _options);
        if (error is not null)
            throw new InvalidOperationException(error);

        return configuration;
    }

    /// <inheritdoc />
    public async Task<NexusAuthTokenResult> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var configuration = await FetchConfigurationAsync(ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = OpenIdConnectGrantTypes.AuthorizationCode,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        };

        return await SendTokenRequestAsync(
            RequireEndpoint(configuration.TokenEndpoint, "token"),
            form,
            ct);
    }

    /// <inheritdoc />
    public async Task<NexusAuthTokenResult> RefreshTokensAsync(
        string refreshToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var configuration = await FetchConfigurationAsync(ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = OpenIdConnectGrantTypes.RefreshToken,
            ["refresh_token"] = refreshToken
        };

        return await SendTokenRequestAsync(
            RequireEndpoint(configuration.TokenEndpoint, "token"),
            form,
            ct);
    }

    /// <inheritdoc />
    public async Task<AccessTokenIntrospectionResult> IntrospectAccessTokenAsync(
        string accessToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var configuration = await FetchConfigurationAsync(ct);
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
            return new AccessTokenIntrospectionResult(active, subject, clientId);
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

        var configuration = await FetchConfigurationAsync(ct);
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
        AddClientAuthentication(form, out var authorization);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Authorization = authorization;

        using var client = httpClientFactory.CreateClient(HttpClientName);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private void AddClientAuthentication(
        IDictionary<string, string> form,
        out AuthenticationHeaderValue? authorization)
    {
        authorization = null;
        switch (_options.ClientAuthenticationMethod)
        {
            case ClientAuthenticationMethod.ClientSecretBasic:
                authorization = CreateBasicAuthenticationHeader(
                    _options.ClientId,
                    RequireClientSecret());
                break;
            case ClientAuthenticationMethod.ClientSecretPost:
                form["client_id"] = _options.ClientId;
                form["client_secret"] = RequireClientSecret();
                break;
            case ClientAuthenticationMethod.None:
                form["client_id"] = _options.ClientId;
                break;
            default:
                throw new InvalidOperationException("ClientAuthenticationMethod is invalid.");
        }
    }

    private string RequireClientSecret()
    {
        if (string.IsNullOrEmpty(_options.ClientSecret))
            throw new InvalidOperationException(
                "ClientSecret is required for the selected client authentication method.");
        return _options.ClientSecret;
    }

    private static AuthenticationHeaderValue CreateBasicAuthenticationHeader(
        string clientId,
        string clientSecret)
    {
        var encodedClientId = WebUtility.UrlEncode(clientId);
        var encodedClientSecret = WebUtility.UrlEncode(clientSecret);
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{encodedClientId}:{encodedClientSecret}"));
        return new AuthenticationHeaderValue("Basic", credentials);
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

    private static NexusAuthProtocolException? FindProtocolException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NexusAuthProtocolException protocolException)
                return protocolException;
        }

        return null;
    }
}

internal static class NexusAuthProtocolResponse
{
    public static NexusAuthProtocolException InvalidResponse(
        HttpStatusCode statusCode,
        Exception? innerException = null)
    {
        return new NexusAuthProtocolException("invalid_response", statusCode, innerException);
    }

    public static void ThrowIfOAuthError(string body, HttpStatusCode statusCode)
    {
        if (TryReadOAuthError(body, out var error))
            throw new NexusAuthProtocolException(error, statusCode);
    }

    public static NexusAuthProtocolException FromResponse(
        string body,
        HttpStatusCode statusCode)
    {
        return TryReadOAuthError(body, out var error)
            ? new NexusAuthProtocolException(error, statusCode)
            : new NexusAuthProtocolException("http_error", statusCode);
    }

    private static bool TryReadOAuthError(string body, out string error)
    {
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var errorProperty)
                && errorProperty.ValueKind == JsonValueKind.String)
            {
                error = errorProperty.GetString() ?? string.Empty;
                return !string.IsNullOrEmpty(error);
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
