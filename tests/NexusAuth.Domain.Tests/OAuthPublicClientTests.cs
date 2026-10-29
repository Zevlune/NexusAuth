using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class OAuthPublicClientTests
{
    [Fact]
    public void Create_allows_none_without_credentials_or_jwks()
    {
        var client = CreatePublicClient();

        Assert.Equal(OAuthClientAuthenticationMethods.None, OAuthClient.TokenEndpointAuthMethodNone);
        Assert.Equal(OAuthClientAuthenticationMethods.None, client.TokenEndpointAuthMethod);
        Assert.Empty(client.ClientSecrets);
        Assert.Null(client.Jwks);
        Assert.Null(client.JwksUri);
        Assert.True(client.AllowsTokenEndpointAuthMethod(OAuthClientAuthenticationMethods.None));
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
            tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.None,
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
            tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.None,
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
            allowedGrantTypes: [OAuthGrantTypes.WeChatMiniProgram, OAuthGrantTypes.RefreshToken]);

        client.Update(tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.None);

        Assert.Equal(OAuthClientAuthenticationMethods.None, client.TokenEndpointAuthMethod);
        Assert.Empty(client.ClientSecrets);
    }

    [Fact]
    public void Create_rejects_none_without_the_supported_public_client_grant()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => OAuthClient.Create(
            "public-client",
            "Public Client",
            allowedGrantTypes: [OAuthGrantTypes.AuthorizationCode],
            tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.None));

        Assert.Contains("requires the WeChat mini program grant", exception.Message);
    }

    [Fact]
    public void Update_rejects_client_secret_for_none_without_mutating_client()
    {
        var client = CreatePublicClient();
        var secret = OAuthClientSecret.CreateSharedSecret(Guid.NewGuid(), "client-secret");

        Assert.Throws<InvalidOperationException>(() => client.Update(clientSecrets: [secret]));

        Assert.Empty(client.ClientSecrets);
        Assert.Equal(OAuthClientAuthenticationMethods.None, client.TokenEndpointAuthMethod);
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
            allowedGrantTypes: [OAuthGrantTypes.WeChatMiniProgram, OAuthGrantTypes.RefreshToken],
            tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.None);
    }
}
