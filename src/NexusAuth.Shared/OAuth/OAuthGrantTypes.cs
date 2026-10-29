namespace NexusAuth.Shared.OAuth;

/// <summary>OAuth grant type values used by NexusAuth and its clients.</summary>
public static class OAuthGrantTypes
{
    public const string AuthorizationCode = "authorization_code";
    public const string ClientCredentials = "client_credentials";
    public const string RefreshToken = "refresh_token";
    public const string DeviceCode = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>NexusAuth extension grant; this is not a standard OAuth grant.</summary>
    public const string WeChatMiniProgram = "urn:nexusauth:grant-type:wechat_miniprogram";
}
