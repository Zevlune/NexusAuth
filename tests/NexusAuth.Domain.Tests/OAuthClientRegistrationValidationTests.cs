using NexusAuth.Domain.AggregateRoots.OAuthClients;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class OAuthClientRegistrationValidationTests
{
    private const string PublicRsaJwks =
        "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"k1\",\"n\":\"sXchDaQebHnPiGvyDOAT4saGEUetSyo9MKLOoWFsueri23bOdgWp4Dy1WlUzewbgBHod5pcM9H95GQRV3JDXboIRROSBigeC5yjU1hGzHHyXss8UDprecbAYxknTcQkhslANGRUZmdTOQ5qTRsLAt6BTYuyvVRdhS8exSZEy_c4gs_7svlJJQ4H9_NxsiIoLwAEk7-Q3UXERGYw_75IDrGA84-lA_-Ct4eTlXHBIY2EaV7t7LjJaynVJCpkv4LKjTTAumiGUIuQhrNhZLuF_RJLqHpM2kgWFLU7-VTdL1VbC2tejvcI2BlMkEpk1BzBZI0KQB0GaDWFLN-aEAw3vRw\",\"e\":\"AQAB\"}]}";

    [Theory]
    [InlineData("https://client.example/callback")]
    [InlineData("http://localhost:5173/signin-oidc")]
    [InlineData("http://127.0.0.1:8080/callback")]
    [InlineData("com.example.app:/oauth2redirect")]
    [InlineData("myapp://callback")]
    public void Create_accepts_absolute_redirect_uris_including_private_use_schemes(string redirectUri)
    {
        var client = OAuthClient.Create("web-client", "Web", redirectUris: [redirectUri], postLogoutRedirectUris: [redirectUri]);

        Assert.Equal([redirectUri], client.RedirectUris);
        Assert.Equal([redirectUri], client.PostLogoutRedirectUris);
    }

    [Theory]
    [InlineData("/callback")]
    [InlineData("callback")]
    [InlineData("https://client.example/callback#fragment")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    public void Create_rejects_relative_fragment_and_dangerous_redirect_uris(string redirectUri)
    {
        Assert.Throws<ArgumentException>(() => OAuthClient.Create("web-client", "Web", redirectUris: [redirectUri]));
        Assert.Throws<ArgumentException>(() => OAuthClient.Create("web-client", "Web", postLogoutRedirectUris: [redirectUri]));
    }

    [Fact]
    public void Update_validates_submitted_redirect_uris_only()
    {
        var client = OAuthClient.Create("web-client", "Web", redirectUris: ["https://client.example/callback"]);

        Assert.Throws<ArgumentException>(() => client.Update(redirectUris: ["javascript:alert(1)"]));
        Assert.Throws<ArgumentException>(() => client.Update(postLogoutRedirectUris: ["https://client.example/#x"]));
        Assert.Equal(["https://client.example/callback"], client.RedirectUris);

        client.Update(clientName: "Renamed");
        client.Update(redirectUris: ["https://client.example/other"]);
        Assert.Equal(["https://client.example/other"], client.RedirectUris);
    }

    [Fact]
    public void Jwks_uri_is_rejected_because_remote_key_sets_are_not_supported()
    {
        var exception = Assert.Throws<ArgumentException>(() => OAuthClient.Create(
            "pk-client",
            "Private key client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
            jwksUri: "https://client.example/jwks.json"));
        Assert.Contains("jwks_uri is not supported yet; provide jwks", exception.Message);

        var client = OAuthClient.Create(
            "pk-client",
            "Private key client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
            jwks: PublicRsaJwks);
        Assert.Throws<ArgumentException>(() => client.Update(jwks: string.Empty, jwksUri: "https://client.example/jwks.json"));
        Assert.Throws<ArgumentException>(() => client.SetJwks(null, "https://client.example/jwks.json"));

        // Clearing jwks_uri (as the Workbench bootstrap does) stays allowed.
        client.Update(jwksUri: string.Empty);
        Assert.Null(client.JwksUri);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"keys\":{}}")]
    [InlineData("{\"keys\":[]}")]
    [InlineData("{\"keys\":[{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"a\",\"y\":\"b\"}]}")]
    [InlineData("{\"keys\":[{\"kty\":\"RSA\",\"n\":\"\",\"e\":\"AQAB\"}]}")]
    [InlineData("{\"keys\":[{\"kty\":\"RSA\",\"n\":\"abc\",\"e\":\"AQAB\",\"d\":\"secret\"}]}")]
    [InlineData("{\"keys\":[{\"kty\":\"oct\",\"k\":\"c2VjcmV0\"},{\"kty\":\"RSA\",\"n\":\"abc\",\"e\":\"AQAB\"}]}")]
    public void Invalid_or_private_jwks_are_rejected_on_save(string jwks)
    {
        Assert.Throws<ArgumentException>(() => OAuthClient.Create(
            "pk-client",
            "Private key client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
            jwks: jwks));

        var client = OAuthClient.Create(
            "pk-client",
            "Private key client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
            jwks: PublicRsaJwks);
        Assert.Throws<ArgumentException>(() => client.Update(jwks: jwks));
        Assert.Throws<ArgumentException>(() => client.SetJwks(jwks));
        Assert.Equal(PublicRsaJwks, client.Jwks);
    }

    [Fact]
    public void Public_rsa_jwks_is_accepted()
    {
        var client = OAuthClient.Create(
            "pk-client",
            "Private key client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
            jwks: PublicRsaJwks);

        Assert.Equal(PublicRsaJwks, client.Jwks);
    }
}
