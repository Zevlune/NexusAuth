using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Application.Services.Authorization;
using NexusAuth.Application.Services.OIDC;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Host.Controllers;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// prompt=login and max_age must force exactly one re-authentication instead of looping forever,
/// and an existing cookie session must still satisfy the current user security policy.
/// </summary>
public sealed class AuthorizeReauthenticationTests : IClassFixture<AuthorizeReauthenticationTests.SharedUser>
{
    private const string RedirectUri = "https://client.example/callback";
    private readonly IDataProtectionProvider dataProtection = new EphemeralDataProtectionProvider();
    private readonly User user;

    public AuthorizeReauthenticationTests(SharedUser shared)
    {
        user = shared.User;
    }

    [Fact]
    public async Task Prompt_login_forces_one_reauthentication_and_then_issues_a_code()
    {
        var staleSession = Principal(DateTimeOffset.UtcNow.AddHours(-1));
        var first = CreateController(staleSession, AuthorizeUrl("prompt=login"), out var firstAuthentication);

        var loginRedirect = Assert.IsType<RedirectResult>(await InvokeAsync(first));

        Assert.Equal(1, firstAuthentication.SignOutCount);
        var returnUrl = ExtractReturnUrl(loginRedirect.Url);
        var returnQuery = QueryHelpers.ParseQuery(new Uri("https://issuer.example" + returnUrl).Query);
        Assert.Equal("login", returnQuery["prompt"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(returnQuery[AuthorizeController.ReauthenticationParameterName]));

        // The user signs in again and the login page redirects back to the same authorize URL.
        var freshSession = Principal(DateTimeOffset.UtcNow);
        var second = CreateController(freshSession, returnUrl, out var secondAuthentication);

        var callback = Assert.IsType<RedirectResult>(await InvokeAsync(second));

        Assert.Equal(0, secondAuthentication.SignOutCount);
        Assert.StartsWith(RedirectUri, callback.Url);
        Assert.Equal("code-123", QueryHelpers.ParseQuery(new Uri(callback.Url).Query)["code"].ToString());
    }

    [Fact]
    public async Task Anonymous_prompt_login_request_needs_only_the_first_login()
    {
        var anonymous = CreateController(new ClaimsPrincipal(new ClaimsIdentity()), AuthorizeUrl("prompt=login"), out var firstAuthentication);

        var returnUrl = ExtractReturnUrl(Assert.IsType<RedirectResult>(await InvokeAsync(anonymous)).Url);

        Assert.Equal(0, firstAuthentication.SignOutCount);
        Assert.Contains(AuthorizeController.ReauthenticationParameterName, returnUrl);
        var second = CreateController(Principal(DateTimeOffset.UtcNow), returnUrl, out var secondAuthentication);
        var callback = Assert.IsType<RedirectResult>(await InvokeAsync(second));
        Assert.StartsWith(RedirectUri, callback.Url);
        Assert.Equal(0, secondAuthentication.SignOutCount);
    }

    [Fact]
    public async Task Reauthentication_marker_does_not_satisfy_a_session_older_than_the_marker()
    {
        var first = CreateController(Principal(DateTimeOffset.UtcNow.AddHours(-1)), AuthorizeUrl("prompt=login"), out _);
        var returnUrl = ExtractReturnUrl(Assert.IsType<RedirectResult>(await InvokeAsync(first)).Url);

        var replayWithOldSession = CreateController(Principal(DateTimeOffset.UtcNow.AddHours(-1)), returnUrl, out var authentication);
        var result = Assert.IsType<RedirectResult>(await InvokeAsync(replayWithOldSession));

        Assert.StartsWith("/account/login?returnUrl=", result.Url);
        Assert.Equal(1, authentication.SignOutCount);
    }

    [Fact]
    public async Task Tampered_reauthentication_marker_is_ignored()
    {
        var url = AuthorizeUrl("prompt=login") + $"&{AuthorizeController.ReauthenticationParameterName}=forged";
        var controller = CreateController(Principal(DateTimeOffset.UtcNow), url, out var authentication);

        var result = Assert.IsType<RedirectResult>(await InvokeAsync(controller));

        Assert.StartsWith("/account/login?returnUrl=", result.Url);
        Assert.Equal(1, authentication.SignOutCount);
        // The forged value is replaced by a freshly protected marker.
        Assert.DoesNotContain("forged", ExtractReturnUrl(result.Url));
    }

    [Fact]
    public async Task Max_age_zero_terminates_after_one_reauthentication()
    {
        var first = CreateController(Principal(DateTimeOffset.UtcNow.AddSeconds(-30)), AuthorizeUrl("max_age=0"), out var firstAuthentication);
        var returnUrl = ExtractReturnUrl(Assert.IsType<RedirectResult>(await InvokeAsync(first)).Url);
        Assert.Equal(1, firstAuthentication.SignOutCount);
        var loggedInAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Cross at least one whole-second boundary so max_age=0 alone can no longer be satisfied.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        var withoutMarker = CreateController(Principal(loggedInAt), AuthorizeUrl("max_age=0"), out _);
        Assert.StartsWith("/account/login?returnUrl=", Assert.IsType<RedirectResult>(await InvokeAsync(withoutMarker)).Url);

        var withMarker = CreateController(Principal(loggedInAt), returnUrl, out var authentication);
        var callback = Assert.IsType<RedirectResult>(await InvokeAsync(withMarker));
        Assert.StartsWith(RedirectUri, callback.Url);
        Assert.Equal(0, authentication.SignOutCount);
    }

    [Fact]
    public async Task Max_age_is_compared_in_whole_seconds()
    {
        var controller = CreateController(Principal(DateTimeOffset.UtcNow.AddSeconds(-3)), AuthorizeUrl("max_age=5"), out var authentication);

        var callback = Assert.IsType<RedirectResult>(await InvokeAsync(controller));

        Assert.StartsWith(RedirectUri, callback.Url);
        Assert.Equal(0, authentication.SignOutCount);
    }

    [Fact]
    public async Task Prompt_none_with_expired_max_age_returns_login_required()
    {
        var controller = CreateController(Principal(DateTimeOffset.UtcNow.AddMinutes(-10)), AuthorizeUrl("prompt=none&max_age=60"), out var authentication);

        var callback = Assert.IsType<RedirectResult>(await InvokeAsync(controller));

        Assert.Equal("login_required", QueryHelpers.ParseQuery(new Uri(callback.Url).Query)["error"].ToString());
        Assert.Equal(0, authentication.SignOutCount);
    }

    [Fact]
    public async Task Consent_redirect_carries_the_satisfied_reauthentication_marker()
    {
        var first = CreateController(Principal(DateTimeOffset.UtcNow.AddHours(-1)), AuthorizeUrl("prompt=login%20consent"), out _);
        var returnUrl = ExtractReturnUrl(Assert.IsType<RedirectResult>(await InvokeAsync(first)).Url);

        var store = new InMemoryConsentStore();
        var second = CreateController(Principal(DateTimeOffset.UtcNow), returnUrl, out _, consentStore: store);
        var consent = Assert.IsType<RedirectResult>(await InvokeAsync(second));

        Assert.StartsWith("/consent?", consent.Url);
        var consentQuery = QueryHelpers.ParseQuery(new Uri("https://issuer.example" + consent.Url).Query);
        var savedQuery = QueryHelpers.ParseQuery(store.GetRequest(consentQuery["id"].ToString()).Query);
        Assert.False(string.IsNullOrWhiteSpace(savedQuery[AuthorizeController.ReauthenticationParameterName]));
    }

    [Fact]
    public async Task Existing_session_of_a_policy_blocked_user_receives_access_denied()
    {
        var policy = new TestSecurityPolicy();
        policy.BlockedUsers.Add(user.Id);
        var controller = CreateController(Principal(DateTimeOffset.UtcNow), AuthorizeUrl(null), out _, policy);

        var callback = Assert.IsType<RedirectResult>(await InvokeAsync(controller));

        var query = QueryHelpers.ParseQuery(new Uri(callback.Url).Query);
        Assert.StartsWith(RedirectUri, callback.Url);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.Equal("state-1", query["state"].ToString());
    }

    private static string AuthorizeUrl(string? extra)
    {
        var url = "/connect/authorize?response_type=code&client_id=web-client"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid&state=state-1";
        return string.IsNullOrEmpty(extra) ? url : url + "&" + extra;
    }

    private ClaimsPrincipal Principal(DateTimeOffset authenticatedAt) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("auth_time", authenticatedAt.ToUnixTimeSeconds().ToString()),
        ], AppWebModule.AuthenticationScheme));

    private AuthorizeController CreateController(
        ClaimsPrincipal principal,
        string pathAndQuery,
        out RecordingSignOutAuthenticationService authentication,
        TestSecurityPolicy? policy = null,
        NexusAuth.Domain.Repositories.IConsentStore? consentStore = null)
    {
        authentication = new RecordingSignOutAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, User = principal };
        var uri = new Uri("https://issuer.example" + pathAndQuery);
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);

        var authorizationService = HardeningProxy.Create<IAuthorizationService>((method, _) => method.Name switch
        {
            nameof(IAuthorizationService.GenerateCodeAsync) => "code-123",
            nameof(IAuthorizationService.ParseRequestedClaims) => OidcRequestedClaims.Empty,
            _ => null,
        });
        var users = HardeningProxy.Create<IUserService>((method, args) =>
            method.Name == nameof(IUserService.FindByIdAsync) && (Guid)args![0]! == user.Id ? user : null);
        var controller = new AuthorizeController(
            authorizationService,
            HardeningClientServices.AcceptingAuthorizeRequests(),
            dataProtection,
            users,
            (policy ?? new TestSecurityPolicy()).Service,
            consentStore: consentStore);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static Task<IActionResult> InvokeAsync(AuthorizeController controller)
    {
        var query = controller.Request.Query;
        string? Value(string key) => query.TryGetValue(key, out var value) ? value.ToString() : null;
        return controller.Authorize(
            Value("response_type"),
            Value("client_id"),
            Value("redirect_uri"),
            Value("scope"),
            Value("state"),
            Value("nonce"),
            Value("prompt"),
            int.TryParse(Value("max_age"), out var maxAge) ? maxAge : null,
            Value("response_mode"),
            Value("claims"),
            Value("code_challenge"),
            Value("code_challenge_method"));
    }

    private static string ExtractReturnUrl(string loginRedirect)
    {
        Assert.StartsWith("/account/login?returnUrl=", loginRedirect);
        var returnUrl = QueryHelpers.ParseQuery(new Uri("https://issuer.example" + loginRedirect).Query)["returnUrl"].ToString();
        Assert.StartsWith("/connect/authorize?", returnUrl);
        return returnUrl;
    }

    public sealed class SharedUser
    {
        public User User { get; } = User.Create($"reauth-{Guid.NewGuid():N}", "Test-password-123!", "Reauth User");
    }
}
