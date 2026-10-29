namespace NexusAuth.Shared.OAuth;

/// <summary>Registered OAuth client authentication method values.</summary>
public static class OAuthClientAuthenticationMethods
{
    public const string None = "none";
    public const string ClientSecretBasic = "client_secret_basic";
    public const string ClientSecretPost = "client_secret_post";
    public const string ClientSecretJwt = "client_secret_jwt";
    public const string PrivateKeyJwt = "private_key_jwt";
}
