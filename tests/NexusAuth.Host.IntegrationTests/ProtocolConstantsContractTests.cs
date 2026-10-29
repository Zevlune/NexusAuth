using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Host.Controllers;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class ProtocolConstantsContractTests
{
    [Fact]
    public void Discovery_keeps_the_existing_protocol_metadata_wire_shape()
    {
        var signingProvider = new HardeningSigningCredentialsProvider(
            new SymmetricSecurityKey(new byte[32]));
        var controller = new OpenIdController(
            signingCredentialsProvider: signingProvider,
            idTokenHintValidator: null!,
            tokenService: null!,
            userService: null!,
            clientService: null!,
            deviceAuthorizationService: null!,
            sessionService: null!,
            antiforgery: null!,
            jwtOptions: Options.Create(new JwtOptions { Issuer = "https://issuer.example" }),
            weChatOptions: Options.Create(new WeChatMiniProgramOptions { Enabled = false }));

        var result = Assert.IsType<OkObjectResult>(controller.Discovery());
        var discovery = JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal(
            [
                "https://issuer.example/connect/authorize",
                "https://issuer.example/connect/token",
                "https://issuer.example/connect/userinfo",
                "https://issuer.example/.well-known/jwks.json",
                "https://issuer.example/connect/revocation",
                "https://issuer.example/connect/introspect",
                "https://issuer.example/connect/deviceauthorization",
                "https://issuer.example/connect/endsession",
            ],
            new[]
            {
                "authorization_endpoint",
                "token_endpoint",
                "userinfo_endpoint",
                "jwks_uri",
                "revocation_endpoint",
                "introspection_endpoint",
                "device_authorization_endpoint",
                "end_session_endpoint",
            }.Select(name => discovery.GetProperty(name).GetString()!));
        Assert.Equal(
            ["openid", "profile", "email", "phone", "offline_access"],
            ReadStringArray(discovery, "scopes_supported"));
        Assert.Equal(["code"], ReadStringArray(discovery, "response_types_supported"));
        Assert.Equal(["query", "form_post"], ReadStringArray(discovery, "response_modes_supported"));
        Assert.Equal(
            [
                "authorization_code",
                "client_credentials",
                "refresh_token",
                "urn:ietf:params:oauth:grant-type:device_code",
            ],
            ReadStringArray(discovery, "grant_types_supported"));
        Assert.Equal(
            [
                "client_secret_post",
                "client_secret_basic",
                "client_secret_jwt",
                "private_key_jwt",
            ],
            ReadStringArray(discovery, "token_endpoint_auth_methods_supported"));
        Assert.Equal(["S256"], ReadStringArray(discovery, "code_challenge_methods_supported"));
        Assert.Equal(
            ["none", "login", "consent"],
            ReadStringArray(discovery, "prompt_values_supported"));
    }

    [Fact]
    public void OAuthClient_keeps_its_public_protocol_aliases()
    {
        Assert.Equal("none", OAuthClient.TokenEndpointAuthMethodNone);
        Assert.Equal("client_secret_basic", OAuthClient.TokenEndpointAuthMethodClientSecretBasic);
        Assert.Equal("client_secret_post", OAuthClient.TokenEndpointAuthMethodClientSecretPost);
        Assert.Equal("client_secret_jwt", OAuthClient.TokenEndpointAuthMethodClientSecretJwt);
        Assert.Equal("private_key_jwt", OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt);
        Assert.Equal(
            "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            OAuthClient.ClientAssertionTypeJwtBearer);
    }

    private static string[] ReadStringArray(JsonElement document, string propertyName) =>
        document.GetProperty(propertyName)
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
}
