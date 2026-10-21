using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class OAuthPublicClientTests
{
    private const string WeChatGrantType = "urn:nexusauth:grant-type:wechat_miniprogram";

    [Fact]
    public void Create_allows_none_without_credentials_or_jwks()
    {
        var client = CreatePublicClient();

        Assert.Equal("none", OAuthClient.TokenEndpointAuthMethodNone);
        Assert.Equal(OAuthClient.TokenEndpointAuthMethodNone, client.TokenEndpointAuthMethod);
        Assert.Empty(client.ClientSecrets);
        Assert.Null(client.Jwks);
        Assert.Null(client.JwksUri);
        Assert.True(client.AllowsTokenEndpointAuthMethod(OAuthClient.TokenEndpointAuthMethodNone));
        Assert.False(client.AllowsClientSecretAuthentication());
        Assert.False(client.RequiresPrivateKeyJwtAuthentication());
    }

    [Fact]
    public void Create_rejects_client_secret_for_none()
    {
        var secret = OAuthClientSecret.CreateSharedSecret(Guid.NewGuid(), "client-secret");

        var exception = Assert.Throws<InvalidOperationException>(() => OAuthClient.Create(
            "public-client",
            "Public Client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodNone,
            clientSecrets: [secret]));

        Assert.Contains("cannot have client secrets", exception.Message);
    }

    [Theory]
    [InlineData("{\"keys\":[]}", null)]
    [InlineData(null, "https://client.example/jwks")]
    public void Create_rejects_jwks_metadata_for_none(string? jwks, string? jwksUri)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => OAuthClient.Create(
            "public-client",
            "Public Client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodNone,
            jwks: jwks,
            jwksUri: jwksUri));

        Assert.Contains("does not use jwks", exception.Message);
    }

    [Fact]
    public void Update_can_switch_credential_less_client_to_none()
    {
        var client = OAuthClient.Create(
            "public-client",
            "Public Client",
            allowedGrantTypes: [WeChatGrantType, "refresh_token"]);

        client.Update(tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodNone);

        Assert.Equal(OAuthClient.TokenEndpointAuthMethodNone, client.TokenEndpointAuthMethod);
        Assert.Empty(client.ClientSecrets);
    }

    [Fact]
    public void Create_rejects_none_without_the_supported_public_client_grant()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => OAuthClient.Create(
            "public-client",
            "Public Client",
            allowedGrantTypes: ["authorization_code"],
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodNone));

        Assert.Contains("requires the WeChat mini program grant", exception.Message);
    }

    [Fact]
    public void Update_rejects_client_secret_for_none_without_mutating_client()
    {
        var client = CreatePublicClient();
        var secret = OAuthClientSecret.CreateSharedSecret(Guid.NewGuid(), "client-secret");

        Assert.Throws<InvalidOperationException>(() => client.Update(clientSecrets: [secret]));

        Assert.Empty(client.ClientSecrets);
        Assert.Equal(OAuthClient.TokenEndpointAuthMethodNone, client.TokenEndpointAuthMethod);
    }

    [Fact]
    public void SetJwks_rejects_jwks_for_none_without_mutating_client()
    {
        var client = CreatePublicClient();

        Assert.Throws<InvalidOperationException>(() =>
            client.SetJwks("{\"keys\":[]}", "https://client.example/jwks"));

        Assert.Null(client.Jwks);
        Assert.Null(client.JwksUri);
    }

    private static OAuthClient CreatePublicClient()
    {
        return OAuthClient.Create(
            "public-client",
            "Public Client",
            allowedGrantTypes: [WeChatGrantType, "refresh_token"],
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodNone);
    }
}
