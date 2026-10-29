using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Host.Controllers;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class OpenIdControllerProtocolTests
{
    private const string AddressClaims =
        "{\"userinfo\":{\"address\":null}}";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Discovery_advertises_public_client_authentication_only_for_the_enabled_wechat_grant(
        bool weChatEnabled,
        bool expectPublicClientAuthentication)
    {
        var controller = CreateOpenIdController(
            weChatEnabled: weChatEnabled);

        var result = Assert.IsType<OkObjectResult>(controller.Discovery());
        var discovery = JsonSerializer.SerializeToElement(result.Value);
        var grantTypes = ReadStringArray(discovery, "grant_types_supported");
        var authenticationMethods = ReadStringArray(discovery, "token_endpoint_auth_methods_supported");

        Assert.Equal(expectPublicClientAuthentication, grantTypes.Contains(WeChatMiniProgramGrantTypes.GrantType));
        Assert.Equal(expectPublicClientAuthentication, authenticationMethods.Contains("none"));
    }

    [Fact]
    public void Discovery_does_not_advertise_address_data_the_provider_cannot_supply()
    {
        var controller = CreateOpenIdController();

        var result = Assert.IsType<OkObjectResult>(controller.Discovery());
        var discovery = JsonSerializer.SerializeToElement(result.Value);

        Assert.DoesNotContain("address", ReadStringArray(discovery, "scopes_supported"));
        Assert.DoesNotContain("address", ReadStringArray(discovery, "claims_supported"));
    }

    [Fact]
    public void Userinfo_supports_get_and_post()
    {
        var method = typeof(OpenIdController).GetMethod(nameof(OpenIdController.UserInfo));

        Assert.NotNull(method);
        Assert.Equal("/connect/userinfo", method.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal("/connect/userinfo", method.GetCustomAttribute<HttpPostAttribute>()?.Template);
    }

    [Fact]
    public async Task Userinfo_without_a_bearer_token_returns_a_bearer_challenge()
    {
        using var harness = new InMemoryTokenServiceHarness();
        var user = CreateUser();
        harness.Add(user);
        var controller = CreateUserInfoController(harness, user);

        var result = await controller.UserInfo(CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("Bearer", controller.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal("invalid_token", JsonSerializer.SerializeToElement(unauthorized.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Userinfo_with_an_invalid_bearer_token_returns_an_invalid_token_challenge()
    {
        using var harness = new InMemoryTokenServiceHarness();
        var user = CreateUser();
        harness.Add(user);
        var controller = CreateUserInfoController(harness, user);
        controller.Request.Headers.Authorization = "Bearer invalid-token";

        var result = await controller.UserInfo(CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("Bearer error=\"invalid_token\"", controller.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal("invalid_token", JsonSerializer.SerializeToElement(unauthorized.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Userinfo_requires_openid_scope_and_returns_an_insufficient_scope_challenge()
    {
        using var harness = new InMemoryTokenServiceHarness();
        var user = CreateUser();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync(
            "profile-only-client",
            "profile email",
            userId: user.Id);
        var controller = CreateUserInfoController(harness, user);
        controller.Request.Headers.Authorization = $"Bearer {accessToken}";

        var result = await controller.UserInfo(CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(
            "Bearer error=\"insufficient_scope\", scope=\"openid\"",
            controller.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal("insufficient_scope", JsonSerializer.SerializeToElement(forbidden.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Userinfo_does_not_emit_a_placeholder_address()
    {
        using var harness = new InMemoryTokenServiceHarness();
        var user = CreateUser();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync(
            "address-client",
            "openid address",
            userId: user.Id,
            claimsJson: AddressClaims);
        var controller = CreateUserInfoController(harness, user);
        controller.Request.Headers.Authorization = $"Bearer {accessToken}";

        var result = await controller.UserInfo(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.SerializeToElement(ok.Value);
        Assert.False(payload.TryGetProperty("address", out _));
    }

    private static OpenIdController CreateOpenIdController(bool weChatEnabled = false)
    {
        var signingProvider = new HardeningSigningCredentialsProvider(
            new SymmetricSecurityKey(new byte[32]));
        return new OpenIdController(
            signingCredentialsProvider: signingProvider,
            idTokenHintValidator: null!,
            tokenService: null!,
            userService: null!,
            clientService: null!,
            deviceAuthorizationService: null!,
            sessionService: null!,
            antiforgery: null!,
            jwtOptions: Options.Create(new JwtOptions { Issuer = InMemoryTokenServiceHarness.Issuer }),
            weChatOptions: Options.Create(new WeChatMiniProgramOptions { Enabled = weChatEnabled }));
    }

    private static OpenIdController CreateUserInfoController(
        InMemoryTokenServiceHarness harness,
        User user)
    {
        var users = HardeningProxy.Create<IUserService>((method, args) =>
            method.Name == nameof(IUserService.FindByIdAsync) && (Guid)args![0]! == user.Id
                ? user
                : null);
        var controller = new OpenIdController(
            signingCredentialsProvider: null!,
            idTokenHintValidator: null!,
            tokenService: harness.Service,
            userService: users,
            clientService: null!,
            deviceAuthorizationService: null!,
            sessionService: null!,
            antiforgery: null!,
            jwtOptions: Options.Create(new JwtOptions { Issuer = InMemoryTokenServiceHarness.Issuer }));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static User CreateUser() =>
        User.Create(
            $"openid-{Guid.NewGuid():N}",
            "Test-password-123!",
            "OpenID User",
            email: "openid@example.com");

    private static string[] ReadStringArray(JsonElement document, string propertyName) =>
        document.GetProperty(propertyName)
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
}
