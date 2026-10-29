using Luck.DDD.Domain.Domain.AggregateRoots;
using System.Linq;
using System.Text.Json;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Domain.AggregateRoots.OAuthClients;

public class OAuthClient : AggregateRootWithIdentity<Guid>
{
    public const string TokenEndpointAuthMethodNone = "none";

    public const string TokenEndpointAuthMethodClientSecretBasic = "client_secret_basic";

    public const string TokenEndpointAuthMethodClientSecretPost = "client_secret_post";

    public const string TokenEndpointAuthMethodClientSecretJwt = "client_secret_jwt";

    public const string TokenEndpointAuthMethodPrivateKeyJwt = "private_key_jwt";

    private const string WeChatMiniProgramGrantType = "urn:nexusauth:grant-type:wechat_miniprogram";

    public const string ClientAssertionTypeJwtBearer = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    public string ClientId { get; private set; } = default!;

    public List<OAuthClientSecret> ClientSecrets { get; private set; } = [];

    public string TokenEndpointAuthMethod { get; private set; } = TokenEndpointAuthMethodClientSecretBasic;

    public string? Jwks { get; private set; }

    public string? JwksUri { get; private set; }

    public string ClientName { get; private set; } = default!;

    public string? Description { get; private set; }

    public List<string> RedirectUris { get; private set; } = [];

    public List<string> PostLogoutRedirectUris { get; private set; } = [];

    public List<string> AllowedScopes { get; private set; } = [];

    public List<string> AllowedGrantTypes { get; private set; } = [];

    public bool RequirePkce { get; private set; }

    public bool RequireConsent { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// EF Core constructor
    /// </summary>
    private OAuthClient(Guid id) : base(id)
    {
    }

    public static OAuthClient Create(
        Guid id,
        string clientId,
        string clientName,
        string? description = null,
        IEnumerable<string>? redirectUris = null,
        IEnumerable<string>? postLogoutRedirectUris = null,
        IEnumerable<string>? allowedScopes = null,
        IEnumerable<string>? allowedGrantTypes = null,
        bool requirePkce = true,
        string? tokenEndpointAuthMethod = null,
        IEnumerable<OAuthClientSecret>? clientSecrets = null,
        string? jwks = null,
        string? jwksUri = null,
        bool requireConsent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        var normalizedSecrets = clientSecrets?.ToList() ?? [];
        var normalizedAuthMethod = NormalizeTokenEndpointAuthMethod(tokenEndpointAuthMethod);
        var normalizedGrantTypes = allowedGrantTypes?.ToList() ?? [];
        var normalizedRedirectUris = redirectUris?.ToList() ?? [];
        var normalizedPostLogoutRedirectUris = postLogoutRedirectUris?.ToList() ?? [];
        ValidateTokenEndpointAuthentication(normalizedAuthMethod, normalizedSecrets, jwks, jwksUri);
        ValidatePublicClientGrantTypes(normalizedAuthMethod, normalizedGrantTypes);
        RejectJwksUri(jwksUri);
        ValidateJwksDocument(NormalizeNullableValue(jwks));
        ValidateRedirectUris(normalizedRedirectUris, "redirect_uri");
        ValidateRedirectUris(normalizedPostLogoutRedirectUris, "post_logout_redirect_uri");

        var client = new OAuthClient(id)
        {
            ClientId = clientId,
            ClientSecrets = normalizedSecrets,
            TokenEndpointAuthMethod = normalizedAuthMethod,
            Jwks = NormalizeNullableValue(jwks),
            JwksUri = NormalizeNullableValue(jwksUri),
            ClientName = clientName,
            Description = description,
            RedirectUris = normalizedRedirectUris,
            PostLogoutRedirectUris = normalizedPostLogoutRedirectUris,
            AllowedScopes = allowedScopes?.ToList() ?? [],
            AllowedGrantTypes = normalizedGrantTypes,
            RequirePkce = requirePkce,
            RequireConsent = requireConsent,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        return client;
    }

    public static OAuthClient Create(
        string clientId,
        string clientName,
        string? description = null,
        IEnumerable<string>? redirectUris = null,
        IEnumerable<string>? postLogoutRedirectUris = null,
        IEnumerable<string>? allowedScopes = null,
        IEnumerable<string>? allowedGrantTypes = null,
        bool requirePkce = true,
        string tokenEndpointAuthMethod = TokenEndpointAuthMethodClientSecretBasic,
        IEnumerable<OAuthClientSecret>? clientSecrets = null,
        string? jwks = null,
        string? jwksUri = null,
        bool requireConsent = false)
    {
        return Create(
            Guid.NewGuid(),
            clientId,
            clientName,
            description,
            redirectUris,
            postLogoutRedirectUris,
            allowedScopes,
            allowedGrantTypes,
            requirePkce,
            tokenEndpointAuthMethod,
            clientSecrets,
            jwks,
            jwksUri,
            requireConsent);
    }

    public bool VerifyClientSecret(string rawSecret)
    {
        return ClientSecrets.Any(secret => secret.VerifySharedSecret(rawSecret));
    }

    public IReadOnlyList<string> GetSharedSecretValues()
    {
        return [.. ClientSecrets
            .Where(secret => secret.IsActive && !string.IsNullOrWhiteSpace(secret.PlainValue))
            .Select(secret => secret.PlainValue!)];
    }

    public bool RequiresPrivateKeyJwtAuthentication()
    {
        return string.Equals(TokenEndpointAuthMethod, TokenEndpointAuthMethodPrivateKeyJwt, StringComparison.Ordinal);
    }

    public bool AllowsClientSecretAuthentication()
    {
        return string.Equals(TokenEndpointAuthMethod, TokenEndpointAuthMethodClientSecretBasic, StringComparison.Ordinal)
            || string.Equals(TokenEndpointAuthMethod, TokenEndpointAuthMethodClientSecretPost, StringComparison.Ordinal)
            || string.Equals(TokenEndpointAuthMethod, TokenEndpointAuthMethodClientSecretJwt, StringComparison.Ordinal);
    }

    public bool IsValidRedirectUri(string uri)
    {
        return RedirectUris.Contains(uri, StringComparer.Ordinal);
    }

    /// <summary>
    /// 用于 OIDC RP-Initiated Logout 场景，校验 RP 传入的 post_logout_redirect_uri 是否已登记。
    /// 主要调用方：Host 层的 /connect/endsession 端点。
    /// </summary>
    public bool IsValidPostLogoutRedirectUri(string uri)
    {
        return PostLogoutRedirectUris.Contains(uri, StringComparer.Ordinal);
    }

    public bool IsGrantTypeAllowed(string grantType)
    {
        return AllowedGrantTypes.Contains(grantType, StringComparer.OrdinalIgnoreCase);
    }

    public void Update(
        string? clientName = null,
        string? description = null,
        IEnumerable<string>? redirectUris = null,
        IEnumerable<string>? postLogoutRedirectUris = null,
        IEnumerable<string>? allowedScopes = null,
        IEnumerable<string>? allowedGrantTypes = null,
        bool? requirePkce = null,
        bool? isActive = null,
        string? tokenEndpointAuthMethod = null,
        IEnumerable<OAuthClientSecret>? clientSecrets = null,
        string? jwks = null,
        string? jwksUri = null,
        bool? requireConsent = null)
    {
        var normalizedAuthMethod = tokenEndpointAuthMethod is null
            ? TokenEndpointAuthMethod
            : NormalizeTokenEndpointAuthMethod(tokenEndpointAuthMethod);
        var updatedGrantTypes = allowedGrantTypes is null
            ? AllowedGrantTypes.ToList()
            : [.. allowedGrantTypes];
        var updatedSecrets = clientSecrets is null
            ? ClientSecrets.ToList()
            : [.. ClientSecrets, .. clientSecrets];
        var updatedJwks = jwks is null ? Jwks : NormalizeNullableValue(jwks);
        var updatedJwksUri = jwksUri is null ? JwksUri : NormalizeNullableValue(jwksUri);
        var updatedRedirectUris = redirectUris?.ToList();
        var updatedPostLogoutRedirectUris = postLogoutRedirectUris?.ToList();

        ValidateTokenEndpointAuthentication(normalizedAuthMethod, updatedSecrets, updatedJwks, updatedJwksUri);
        ValidatePublicClientGrantTypes(normalizedAuthMethod, updatedGrantTypes);
        // 中文注释：只校验本次显式提交的 jwks / jwks_uri / 回调地址，避免历史数据阻塞无关字段的更新。
        RejectJwksUri(jwksUri);
        if (jwks is not null)
            ValidateJwksDocument(updatedJwks);
        if (updatedRedirectUris is not null)
            ValidateRedirectUris(updatedRedirectUris, "redirect_uri");
        if (updatedPostLogoutRedirectUris is not null)
            ValidateRedirectUris(updatedPostLogoutRedirectUris, "post_logout_redirect_uri");

        if (!string.IsNullOrWhiteSpace(clientName))
            ClientName = clientName;
        
        if (description is { } || clientName is { })
            Description = description;
        
        if (updatedRedirectUris is { })
            RedirectUris = updatedRedirectUris;

        if (updatedPostLogoutRedirectUris is { })
            PostLogoutRedirectUris = updatedPostLogoutRedirectUris;
        
        if (allowedScopes is { })
            AllowedScopes = [.. allowedScopes];
        
        AllowedGrantTypes = updatedGrantTypes;
        
        if (requirePkce is { } requirePkceValue)
            RequirePkce = requirePkceValue;
        
        if (requireConsent is { } requireConsentValue)
            RequireConsent = requireConsentValue;

        if (isActive is { } isActiveValue)
            IsActive = isActiveValue;

        TokenEndpointAuthMethod = normalizedAuthMethod;

        ClientSecrets = updatedSecrets;

        Jwks = updatedJwks;
        JwksUri = updatedJwksUri;
    }

    public bool AllowsTokenEndpointAuthMethod(string tokenEndpointAuthMethod)
    {
        return string.Equals(TokenEndpointAuthMethod, tokenEndpointAuthMethod, StringComparison.Ordinal);
    }

    public void SetJwks(string? jwks, string? jwksUri = null)
    {
        var normalizedJwks = NormalizeNullableValue(jwks);
        var normalizedJwksUri = NormalizeNullableValue(jwksUri);

        ValidateTokenEndpointAuthentication(TokenEndpointAuthMethod, ClientSecrets, normalizedJwks, normalizedJwksUri);
        RejectJwksUri(normalizedJwksUri);
        ValidateJwksDocument(normalizedJwks);

        Jwks = normalizedJwks;
        JwksUri = normalizedJwksUri;
    }

    /// <summary>
    /// 中文注释：远程 jwks_uri 拉取尚未实现（同时也会引入 SSRF 面），
    /// 客户端只能登记内联 jwks，避免出现“能保存但永远无法认证”的 private_key_jwt 客户端。
    /// </summary>
    private static void RejectJwksUri(string? jwksUri)
    {
        if (!string.IsNullOrWhiteSpace(jwksUri))
            throw new ArgumentException("jwks_uri is not supported yet; provide jwks.", nameof(jwksUri));
    }

    /// <summary>
    /// 中文注释：保存前校验内联 JWKS：必须是合法 JSON、至少包含一个 RSA 公钥，且不允许携带私钥参数。
    /// 主要调用方：Create / Update / SetJwks。
    /// </summary>
    private static void ValidateJwksDocument(string? jwks)
    {
        if (jwks is null)
            return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jwks);
        }
        catch (JsonException)
        {
            throw new ArgumentException("jwks must be a valid JSON Web Key Set document.", nameof(jwks));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("keys", out var keys)
                || keys.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("jwks must be a JSON object with a 'keys' array.", nameof(jwks));
            }

            var rsaPublicKeyCount = 0;
            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Every jwks entry must be a JSON object.", nameof(jwks));

                if (PrivateJwkParameters.Any(parameter => key.TryGetProperty(parameter, out _)))
                    throw new ArgumentException("jwks must only contain public keys; private key parameters are not allowed.", nameof(jwks));

                if (key.TryGetProperty("kty", out var kty)
                    && kty.ValueKind == JsonValueKind.String
                    && string.Equals(kty.GetString(), "RSA", StringComparison.OrdinalIgnoreCase)
                    && IsNonEmptyString(key, "n")
                    && IsNonEmptyString(key, "e"))
                {
                    rsaPublicKeyCount++;
                }
            }

            if (rsaPublicKeyCount == 0)
                throw new ArgumentException("jwks must contain at least one RSA public key (kty, n, e).", nameof(jwks));
        }
    }

    private static bool IsNonEmptyString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString());
    }

    /// <summary>
    /// 中文注释：回调地址必须是绝对 URI，不允许 fragment（RFC 6749 §3.1.2），
    /// 并拒绝 javascript: / data: / vbscript: / file: 等危险 scheme；
    /// http(s) 以及原生应用使用的自定义私有 scheme（RFC 8252 §7.1）保持允许。
    /// </summary>
    private static void ValidateRedirectUris(IEnumerable<string> uris, string parameterName)
    {
        foreach (var uri in uris)
        {
            // 中文注释：Unix 上 "/path" 会被 Uri 解析成 file:// 绝对地址，这里先要求显式 scheme。
            if (string.IsNullOrWhiteSpace(uri)
                || uri.StartsWith('/')
                || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || string.IsNullOrEmpty(parsed.Scheme))
            {
                throw new ArgumentException($"{parameterName} '{uri}' must be an absolute URI.", parameterName);
            }

            if (uri.Contains('#', StringComparison.Ordinal))
                throw new ArgumentException($"{parameterName} '{uri}' must not contain a fragment.", parameterName);

            if (DangerousRedirectSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"{parameterName} '{uri}' uses a forbidden scheme '{parsed.Scheme}'.", parameterName);
        }
    }

    private static readonly string[] PrivateJwkParameters = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];

    private static readonly string[] DangerousRedirectSchemes = ["javascript", "data", "vbscript", "file"];

    private static void ValidateTokenEndpointAuthentication(
        string tokenEndpointAuthMethod,
        IReadOnlyCollection<OAuthClientSecret> clientSecrets,
        string? jwks,
        string? jwksUri)
    {
        if (!string.Equals(tokenEndpointAuthMethod, TokenEndpointAuthMethodClientSecretBasic, StringComparison.Ordinal)
            && !string.Equals(tokenEndpointAuthMethod, TokenEndpointAuthMethodClientSecretPost, StringComparison.Ordinal)
            && !string.Equals(tokenEndpointAuthMethod, TokenEndpointAuthMethodClientSecretJwt, StringComparison.Ordinal)
            && !string.Equals(tokenEndpointAuthMethod, TokenEndpointAuthMethodPrivateKeyJwt, StringComparison.Ordinal)
            && !string.Equals(tokenEndpointAuthMethod, TokenEndpointAuthMethodNone, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported token_endpoint_auth_method '{tokenEndpointAuthMethod}'.");
        }

        if (string.Equals(tokenEndpointAuthMethod, TokenEndpointAuthMethodNone, StringComparison.Ordinal))
        {
            if (clientSecrets.Count > 0)
                throw new InvalidOperationException("token_endpoint_auth_method 'none' cannot have client secrets.");

            if (!string.IsNullOrWhiteSpace(jwks) || !string.IsNullOrWhiteSpace(jwksUri))
                throw new InvalidOperationException("token_endpoint_auth_method 'none' does not use jwks or jwks_uri.");

            return;
        }

        if (string.Equals(tokenEndpointAuthMethod, TokenEndpointAuthMethodPrivateKeyJwt, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(jwks) && string.IsNullOrWhiteSpace(jwksUri))
                return;

            if (!string.IsNullOrWhiteSpace(jwks) && !string.IsNullOrWhiteSpace(jwksUri))
                throw new InvalidOperationException("private_key_jwt client cannot configure both jwks and jwks_uri.");

            return;
        }

        if (string.Equals(tokenEndpointAuthMethod, TokenEndpointAuthMethodClientSecretJwt, StringComparison.Ordinal)
            && clientSecrets.Any(secret => secret.IsActive && string.IsNullOrWhiteSpace(secret.PlainValue)))
        {
            throw new InvalidOperationException("client_secret_jwt requires retrievable shared secret material.");
        }

        if (!string.IsNullOrWhiteSpace(jwks) || !string.IsNullOrWhiteSpace(jwksUri))
            throw new InvalidOperationException($"token_endpoint_auth_method '{tokenEndpointAuthMethod}' does not use jwks or jwks_uri.");
    }

    private static void ValidatePublicClientGrantTypes(
        string tokenEndpointAuthMethod,
        IReadOnlyCollection<string> allowedGrantTypes)
    {
        if (!string.Equals(tokenEndpointAuthMethod, TokenEndpointAuthMethodNone, StringComparison.Ordinal))
            return;

        var unsupportedGrant = allowedGrantTypes.FirstOrDefault(grantType =>
            !string.Equals(grantType, WeChatMiniProgramGrantType, StringComparison.Ordinal)
            && !string.Equals(grantType, "refresh_token", StringComparison.Ordinal));
        if (unsupportedGrant is not null
            || !allowedGrantTypes.Contains(WeChatMiniProgramGrantType, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "token_endpoint_auth_method 'none' requires the WeChat mini program grant and may only include refresh_token.");
        }
    }

    private static string NormalizeTokenEndpointAuthMethod(string? tokenEndpointAuthMethod)
    {
        return string.IsNullOrWhiteSpace(tokenEndpointAuthMethod)
            ? TokenEndpointAuthMethodClientSecretBasic
            : tokenEndpointAuthMethod.Trim();
    }

    private static string? NormalizeNullableValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
