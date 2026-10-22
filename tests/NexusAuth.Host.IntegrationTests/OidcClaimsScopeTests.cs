using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Host.Controllers;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// The OIDC claims request parameter may only select claims covered by the granted scopes
/// (OIDC Core 5.4/5.5), both for userinfo and for the id_token.
/// </summary>
public sealed class OidcClaimsScopeTests : IClassFixture<OidcClaimsScopeTests.SharedUser>
{
    private const string AllSensitiveClaims =
        "{\"userinfo\":{\"email\":null,\"email_verified\":null,\"phone_number\":null,\"phone_number_verified\":null,\"gender\":null,\"ethnicity\":null,\"nickname\":null,\"address\":null},"
        + "\"id_token\":{\"email\":null,\"phone_number\":null,\"gender\":null,\"ethnicity\":null,\"nickname\":null}}";

    private readonly User user;

    public OidcClaimsScopeTests(SharedUser shared)
    {
        user = shared.User;
    }

    [Fact]
    public async Task Userinfo_claims_parameter_cannot_bypass_missing_scopes()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync("openid-only", "openid", null, user.Id, AllSensitiveClaims);

        var payload = await CallUserInfoAsync(harness, accessToken);

        Assert.Equal(user.Id.ToString(), payload.GetProperty("sub").GetString());
        foreach (var claim in new[] { "email", "email_verified", "phone_number", "phone_number_verified", "gender", "ethnicity", "nickname", "address", "name", "preferred_username" })
            Assert.False(payload.TryGetProperty(claim, out _), $"userinfo must not contain '{claim}' without its scope.");
    }

    [Fact]
    public async Task Userinfo_returns_requested_claims_covered_by_granted_scopes()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync(
            "full-client", "openid profile email phone", null, user.Id, AllSensitiveClaims);

        var payload = await CallUserInfoAsync(harness, accessToken);

        Assert.Equal("alice@example.com", payload.GetProperty("email").GetString());
        Assert.Equal("13800000000", payload.GetProperty("phone_number").GetString());
        Assert.Equal("Female", payload.GetProperty("gender").GetString());
        Assert.Equal("汉族", payload.GetProperty("ethnicity").GetString());
        Assert.Equal("Alice", payload.GetProperty("nickname").GetString());
        Assert.Equal("Alice", payload.GetProperty("name").GetString());
        Assert.Equal(user.Username, payload.GetProperty("preferred_username").GetString());
        // address scope was not granted.
        Assert.False(payload.TryGetProperty("address", out _));
    }

    [Fact]
    public async Task Userinfo_without_claims_parameter_keeps_scope_based_defaults()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync("client", "openid profile email", null, user.Id);

        var payload = await CallUserInfoAsync(harness, accessToken);

        Assert.Equal(user.Username, payload.GetProperty("preferred_username").GetString());
        Assert.Equal("Alice", payload.GetProperty("name").GetString());
        Assert.Equal("alice@example.com", payload.GetProperty("email").GetString());
        Assert.False(payload.TryGetProperty("phone_number", out _));
        Assert.False(payload.TryGetProperty("gender", out _));
        Assert.False(payload.TryGetProperty("ethnicity", out _));
    }

    [Fact]
    public async Task Id_token_claims_parameter_cannot_bypass_missing_scopes()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync("openid-only", "openid", null, user.Id, AllSensitiveClaims);

        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(
            await harness.Service.IssueIdTokenAsync("openid-only", user.Id, "nonce-1", accessToken, AllSensitiveClaims));

        Assert.Equal(user.Id.ToString(), idToken.Subject);
        Assert.Contains(idToken.Claims, claim => claim.Type == "nonce" && claim.Value == "nonce-1");
        foreach (var claim in new[] { "email", "email_verified", "phone_number", "phone_number_verified", "gender", "ethnicity", "nickname", "name", "preferred_username" })
            Assert.DoesNotContain(idToken.Claims, value => value.Type == claim);
    }

    [Fact]
    public async Task Id_token_returns_requested_claims_covered_by_granted_scopes()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var scope = "openid profile email phone";
        var accessToken = await harness.Service.IssueAccessTokenAsync("full-client", scope, null, user.Id, AllSensitiveClaims);

        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(
            await harness.Service.IssueIdTokenAsync("full-client", user.Id, null, accessToken, AllSensitiveClaims));

        Assert.Contains(idToken.Claims, claim => claim.Type == "email" && claim.Value == "alice@example.com");
        Assert.Contains(idToken.Claims, claim => claim.Type == "phone_number" && claim.Value == "13800000000");
        Assert.Contains(idToken.Claims, claim => claim.Type == "gender" && claim.Value == "Female");
        Assert.Contains(idToken.Claims, claim => claim.Type == "ethnicity" && claim.Value == "汉族");
        Assert.Contains(idToken.Claims, claim => claim.Type == "nickname" && claim.Value == "Alice");
        Assert.Contains(idToken.Claims, claim => claim.Type == "preferred_username" && claim.Value == user.Username);
        Assert.Contains(idToken.Claims, claim => claim.Type == "name" && claim.Value == "Alice");
    }

    [Fact]
    public async Task Id_token_profile_scope_alone_does_not_release_email_or_phone()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync("profile-client", "openid profile", null, user.Id, AllSensitiveClaims);

        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(
            await harness.Service.IssueIdTokenAsync("profile-client", user.Id, null, accessToken, AllSensitiveClaims));

        Assert.DoesNotContain(idToken.Claims, claim => claim.Type is "email" or "phone_number");
        Assert.Contains(idToken.Claims, claim => claim.Type == "gender");
    }

    private static async Task<JsonElement> CallUserInfoAsync(InMemoryTokenServiceHarness harness, string accessToken)
    {
        var users = HardeningProxy.Create<IUserService>((method, args) =>
            method.Name == nameof(IUserService.FindByIdAsync) ? harness.FindUser((Guid)args![0]!) : null);
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
        controller.HttpContext.Request.Headers.Authorization = $"Bearer {accessToken}";

        var result = await controller.UserInfo(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    public sealed class SharedUser
    {
        public User User { get; } = User.Create(
            $"alice-{Guid.NewGuid():N}",
            "Test-password-123!",
            "Alice",
            email: "alice@example.com",
            phoneNumber: "13800000000",
            gender: Gender.Female,
            ethnicity: "汉族");
    }
}
