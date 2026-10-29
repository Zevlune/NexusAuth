using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.LoginAudits;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Host.Authentication;
using NexusAuth.Shared.OAuth;
using Xunit;
using Proxy = NexusAuth.Host.IntegrationTests.PasskeyManagementTests.RecordingProxy;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// End-to-end page tests for the account security hardening: TOTP lockout,
/// authenticator management re-verification, password change, login history,
/// security headers, cookie transport and anonymous handler input validation.
/// </summary>
public sealed class AccountSecurityPageTests
{
    [Fact]
    public async Task Totp_step_rejects_a_replayed_flow_token_while_the_account_is_locked()
    {
        using var harness = new Harness(authenticated: false) { TotpEnabled = true };
        using var client = harness.CreateClient();
        var flowToken = await StartPasswordLoginAsync(client);

        harness.User.RegisterFailedLogin(DateTimeOffset.UtcNow, 1, TimeSpan.FromMinutes(5));
        var response = await PostAsync(client, "/account/login?handler=Totp", "/account/login",
            new() { ["FlowToken"] = flowToken, ["TotpCode"] = "123456" });

        response.EnsureSuccessStatusCode();
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Too many failed attempts", page);
        Assert.Equal(0, harness.TotpValidations);
        Assert.Equal(0, harness.SignIns);
        Assert.Contains("UserLocked", harness.AuditReasons);
    }

    [Fact]
    public async Task Invalid_totp_code_is_rejected_and_valid_code_completes_the_login()
    {
        using var harness = new Harness(authenticated: false) { TotpEnabled = true };
        using var client = harness.CreateClient();
        var flowToken = await StartPasswordLoginAsync(client);

        var rejected = await PostAsync(client, "/account/login?handler=Totp", "/account/login",
            new() { ["FlowToken"] = flowToken, ["TotpCode"] = "000000" });
        Assert.Contains("Invalid or already used authenticator code.", WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync()));
        Assert.Equal(1, harness.TotpValidations);
        Assert.Equal(0, harness.SignIns);

        harness.TotpValid = true;
        var accepted = await PostAsync(client, "/account/login?handler=Totp", "/account/login",
            new() { ["FlowToken"] = flowToken, ["TotpCode"] = "123456" });
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal(1, harness.SignIns);
    }

    [Fact]
    public async Task Login_email_code_handler_with_empty_address_returns_a_validation_message()
    {
        using var harness = new Harness(authenticated: false);
        using var client = harness.CreateClient();

        var response = await PostAsync(client, "/account/login?handler=SendEmailCode", "/account/login?mode=email",
            new() { ["Username"] = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("请输入邮箱地址。", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [Theory]
    [InlineData("Send", "", "请输入邮箱地址。")]
    [InlineData("Reset", "", "请输入邮箱地址。")]
    [InlineData("Reset", "user@example.test", "请输入密码。")]
    public async Task Forgot_password_handlers_with_empty_input_return_validation_messages(
        string handler,
        string email,
        string expectedMessage)
    {
        using var harness = new Harness(authenticated: false);
        using var client = harness.CreateClient();

        var response = await PostAsync(client, $"/account/forgot-password?handler={handler}", "/account/forgot-password",
            new() { ["EmailAddress"] = email, ["Code"] = "", ["NewPassword"] = "", ["ConfirmPassword"] = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedMessage, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Two_factor_page_is_not_cacheable()
    {
        using var harness = new Harness();
        using var client = harness.CreateClient();

        var response = await client.GetAsync("/account/two-factor");

        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Adding_an_authenticator_requires_an_existing_totp_code_when_enabled()
    {
        using var harness = new Harness { TotpEnabled = true };
        using var client = harness.CreateClient();

        var withoutCode = await PostAsync(client, "/account/two-factor?handler=Begin", "/account/two-factor",
            new() { ["CurrentPassword"] = "correct" });
        Assert.Equal(HttpStatusCode.OK, withoutCode.StatusCode);
        Assert.Equal(0, harness.Enrollments);

        harness.TotpValid = true;
        var withCode = await PostAsync(client, "/account/two-factor?handler=Begin", "/account/two-factor",
            new() { ["CurrentPassword"] = "correct", ["CurrentTotpCode"] = "123456" });
        Assert.Equal(HttpStatusCode.OK, withCode.StatusCode);
        Assert.Equal(1, harness.Enrollments);
    }

    [Fact]
    public async Task Removing_an_authenticator_requires_an_existing_totp_code()
    {
        using var harness = new Harness { TotpEnabled = true };
        using var client = harness.CreateClient();

        var withoutCode = await PostAsync(client, "/account/two-factor?handler=Disable", "/account/two-factor",
            new() { ["CurrentPassword"] = "correct", ["CredentialId"] = harness.CredentialId.ToString() });
        Assert.Equal(HttpStatusCode.OK, withoutCode.StatusCode);
        Assert.Equal(0, harness.Disables);

        harness.TotpValid = true;
        var withCode = await PostAsync(client, "/account/two-factor?handler=Disable", "/account/two-factor",
            new() { ["CurrentPassword"] = "correct", ["CurrentTotpCode"] = "123456", ["CredentialId"] = harness.CredentialId.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, withCode.StatusCode);
        Assert.Equal(1, harness.Disables);
    }

    [Fact]
    public async Task Authenticator_management_reverifies_the_password_by_user_id_not_the_stale_name_claim()
    {
        using var harness = new Harness();
        using var client = harness.CreateClient();

        await PostAsync(client, "/account/two-factor?handler=Begin", "/account/two-factor",
            new() { ["CurrentPassword"] = "correct" });

        Assert.Equal(new[] { "alice" }, harness.PasswordIdentifiers);
        Assert.Equal(1, harness.Enrollments);
    }

    [Fact]
    public async Task Enrollment_confirmation_renders_server_side_otpauth_instead_of_posted_values()
    {
        using var harness = new Harness();
        using var client = harness.CreateClient();

        var response = await PostAsync(client, "/account/two-factor?handler=Confirm", "/account/two-factor",
            new()
            {
                ["ProtectedEnrollmentSecret"] = Harness.ServerProtectedSecret,
                ["OtpAuthUri"] = "javascript:alert(1)",
                ["EnrollmentSecret"] = "ATTACKERSECRET",
                ["Code"] = "000000",
            });

        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("javascript:alert(1)", page);
        Assert.DoesNotContain("ATTACKERSECRET", page);
        Assert.Contains(Harness.ServerOtpauthUri, page);
        Assert.Contains(Harness.ServerManualKey, page);
    }

    [Fact]
    public async Task Change_password_requires_totp_when_enabled()
    {
        using var harness = new Harness { TotpEnabled = true };
        using var client = harness.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["CurrentPassword"] = "correct",
            ["NewPassword"] = "NewPassword!42",
            ["ConfirmNewPassword"] = "NewPassword!42",
        };

        var withoutCode = await PostAsync(client, "/account/change-password", "/account/change-password", form);
        Assert.Equal(HttpStatusCode.OK, withoutCode.StatusCode);
        Assert.Equal(0, harness.PasswordChanges);
        Assert.Contains("no-store", withoutCode.Headers.CacheControl?.ToString());

        harness.TotpValid = true;
        form["TotpCode"] = "123456";
        var withCode = await PostAsync(client, "/account/change-password", "/account/change-password", form);
        Assert.Equal(HttpStatusCode.OK, withCode.StatusCode);
        Assert.Equal(1, harness.PasswordChanges);
    }

    [Fact]
    public async Task Login_history_labels_failed_attempts_with_their_reason()
    {
        using var harness = new Harness();
        using var client = harness.CreateClient();

        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/account/login-history"));

        Assert.DoesNotContain("仅显示当前账号最近 20 次成功登录", page);
        Assert.Contains("包括失败记录", page);
        Assert.Contains(">成功<", page);
        Assert.Contains(">失败<", page);
        Assert.Contains("动态验证码错误", page);
    }

    [Fact]
    public async Task Responses_carry_anti_framing_and_content_type_security_headers()
    {
        using var harness = new Harness(authenticated: false);
        using var client = harness.CreateClient();

        var login = await client.GetAsync("/account/login");
        Assert.Equal("DENY", Assert.Single(login.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("frame-ancestors 'none'", Assert.Single(login.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(login.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(login.Headers.GetValues("Referrer-Policy")));

        var authorize = await client.GetAsync(
            $"/connect/authorize?response_type={OAuthResponseTypes.Code}");
        Assert.Equal("no-referrer", Assert.Single(authorize.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(authorize.Headers.GetValues("X-Frame-Options")));
    }

    [Theory]
    [InlineData(null, CookieSecurePolicy.SameAsRequest)]
    [InlineData("SameAsRequest", CookieSecurePolicy.SameAsRequest)]
    [InlineData("always", CookieSecurePolicy.Always)]
    public async Task Sso_cookie_secure_policy_is_configurable(string? policy, CookieSecurePolicy expected)
    {
        using var harness = new Harness(authenticated: false, cookieSecurePolicy: policy);
        using var client = harness.CreateClient();

        // Forms keep working over plain HTTP (e.g. behind a TLS proxy without forwarded
        // headers) even when the SSO cookie is forced to Secure.
        var response = await client.GetAsync("/account/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var services = harness.Factory.Services;
        var cookieOptions = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AppWebModule.AuthenticationScheme);
        Assert.Equal(expected, cookieOptions.Cookie.SecurePolicy);
        var antiforgery = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>>().Value;
        Assert.Equal(CookieSecurePolicy.SameAsRequest, antiforgery.Cookie.SecurePolicy);
    }

    [Fact]
    public async Task Antiforgery_cookie_is_marked_secure_on_https_requests_by_default()
    {
        using var harness = new Harness(authenticated: false);
        using var client = harness.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/account/login");

        var antiforgeryCookie = response.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));
        Assert.Contains("secure", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Downgrading_cookie_secure_policy_fails_startup()
    {
        using var harness = new Harness(authenticated: false, cookieSecurePolicy: "None");

        Assert.ThrowsAny<Exception>(() => harness.CreateClient());
    }

    [Fact]
    public async Task Swagger_can_be_disabled_by_configuration()
    {
        using var harness = new Harness(authenticated: false, swaggerEnabled: "false");
        using var client = harness.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }

    [Fact]
    public async Task Sso_cookie_validation_rejects_users_denied_by_the_security_policy()
    {
        var user = User.Create("alice", "CurrentPassword!42", "Alice");
        var allowed = await ValidateSsoPrincipalAsync(user, new NexusAuthSecurityOptions());
        var blacklisted = await ValidateSsoPrincipalAsync(user, new NexusAuthSecurityOptions { UserBlacklist = ["alice"] });
        var notWhitelisted = await ValidateSsoPrincipalAsync(user, new NexusAuthSecurityOptions { UserWhitelist = ["bob"] });

        Assert.NotNull(allowed);
        Assert.Null(blacklisted);
        Assert.Null(notWhitelisted);
    }

    private static async Task<ClaimsPrincipal?> ValidateSsoPrincipalAsync(User user, NexusAuthSecurityOptions options)
    {
        var events = new SsoCookieAuthenticationEvents(
            Proxy.Create<ISsoSessionService>((method, _) => method.Name == nameof(ISsoSessionService.IsActiveAsync) ? true : null),
            Proxy.Create<IUserService>((method, _) => method.Name == nameof(IUserService.FindByIdAsync) ? user : null),
            new SecurityPolicyService(Options.Create(options)));
        var services = new ServiceCollection()
            .AddSingleton(Proxy.Create<IAuthenticationService>((_, _) => null))
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("sid", Guid.NewGuid().ToString()),
        ], AppWebModule.AuthenticationScheme));
        var scheme = new AuthenticationScheme(AppWebModule.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var context = new CookieValidatePrincipalContext(
            httpContext,
            scheme,
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, AppWebModule.AuthenticationScheme));

        await events.ValidatePrincipal(context);
        return context.Principal;
    }

    private static async Task<string> StartPasswordLoginAsync(HttpClient client)
    {
        var response = await PostAsync(client, "/account/login", "/account/login",
            new() { ["Username"] = "alice", ["Password"] = "correct" });
        response.EnsureSuccessStatusCode();
        return ExtractHiddenInput(await response.Content.ReadAsStringAsync(), "FlowToken");
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string formPage,
        Dictionary<string, string> form)
    {
        var html = await client.GetStringAsync(formPage);
        form["__RequestVerificationToken"] = ExtractHiddenInput(html, "__RequestVerificationToken");
        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private static string ExtractHiddenInput(string html, string name)
    {
        var match = Regex.Match(html, $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"Hidden input '{name}' was not found.");
        return WebUtility.HtmlDecode(match.Groups["value"].Value);
    }

    private sealed class Harness : IDisposable
    {
        public const string ServerProtectedSecret = "server-protected-secret";
        public const string ServerManualKey = "JBSWY3DPEHPK3PXP";
        public const string ServerOtpauthUri = "otpauth://totp/NexusAuth%3Aalice?secret=JBSWY3DPEHPK3PXP";

        public Harness(bool authenticated = true, string? cookieSecurePolicy = null, string? swaggerEnabled = null)
        {
            Factory = new WebApplicationFactory<AppWebModule>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development")
                    .UseTestSetting("BootstrapAdmin:Username", string.Empty)
                    .UseTestSetting("BootstrapAdmin:Password", string.Empty)
                    .UseTestSetting("Smtp:Enabled", "false");
                if (cookieSecurePolicy is not null)
                    builder.UseTestSetting("Security:CookieSecurePolicy", cookieSecurePolicy);
                if (swaggerEnabled is not null)
                    builder.UseTestSetting("Swagger:Enabled", swaggerEnabled);

                builder.ConfigureTestServices(services =>
                {
                    services.Remove(services.Single(item => item.ImplementationType == typeof(BootstrapAdminHostedService)));
                    if (authenticated)
                    {
                        services.PostConfigure<AuthenticationOptions>(options =>
                            options.SchemeMap[AppWebModule.AuthenticationScheme].HandlerType = typeof(TestAuthenticationHandler));
                        services.AddTransient<TestAuthenticationHandler>();
                    }

                    services.AddSingleton(this);
                    services.AddScoped(_ => Proxy.Create<IUserService>((method, args) =>
                    {
                        switch (method.Name)
                        {
                            case nameof(IUserService.FindByIdAsync):
                                return (Guid)args![0]! == User.Id ? User : null;
                            case nameof(IUserService.ValidateCredentialsAsync):
                                PasswordIdentifiers.Add((string)args![0]!);
                                return (string)args[0]! == User.Username && (string)args[1]! == "correct" ? User : null;
                            case nameof(IUserService.ChangePasswordAsync):
                                PasswordChanges++;
                                return null;
                            default:
                                throw new NotSupportedException(method.Name);
                        }
                    }));
                    services.AddScoped(_ => Proxy.Create<ITotpService>((method, args) =>
                    {
                        switch (method.Name)
                        {
                            case nameof(ITotpService.IsEnabledAsync):
                                return TotpEnabled;
                            case nameof(ITotpService.ValidateAsync):
                                TotpValidations++;
                                return TotpValid;
                            case nameof(ITotpService.GetCredentialsAsync):
                                return (IReadOnlyList<TotpCredentialSummary>)(TotpEnabled
                                    ? [new TotpCredentialSummary(CredentialId, "Phone", true, DateTimeOffset.UtcNow)]
                                    : []);
                            case nameof(ITotpService.BeginEnrollmentAsync):
                                Enrollments++;
                                return new TotpEnrollment(Guid.NewGuid(), ServerProtectedSecret, ServerManualKey, ServerOtpauthUri);
                            case nameof(ITotpService.FindPendingEnrollmentAsync):
                                return (string)args![1]! == ServerProtectedSecret
                                    ? new TotpEnrollment(Guid.NewGuid(), ServerProtectedSecret, ServerManualKey, ServerOtpauthUri)
                                    : null;
                            case nameof(ITotpService.ConfirmEnrollmentAsync):
                                return false;
                            case nameof(ITotpService.DisableAsync):
                                Disables++;
                                return true;
                            default:
                                throw new NotSupportedException(method.Name);
                        }
                    }));
                    services.AddScoped(_ => Proxy.Create<ISsoSessionService>((_, _) => null));
                    services.AddScoped(_ => Proxy.Create<IWebSignInService>((_, _) =>
                    {
                        SignIns++;
                        return null;
                    }));
                    services.AddScoped(_ => Proxy.Create<ILoginAuditService>((method, args) =>
                    {
                        if (method.Name == nameof(ILoginAuditService.RecordAsync))
                        {
                            var record = (LoginAuditRecord)args![0]!;
                            if (record.FailureReason is not null)
                                AuditReasons.Add(record.FailureReason);
                            return null;
                        }

                        if (method.Name == nameof(ILoginAuditService.GetRecentForUserAsync))
                        {
                            return (IReadOnlyList<LoginAuditLogDto>)
                            [
                                new LoginAuditLogDto(Guid.NewGuid(), "alice", User.Id, null, true, null, "203.0.113.1", null, DateTimeOffset.UtcNow),
                                new LoginAuditLogDto(Guid.NewGuid(), "alice", User.Id, null, false, "InvalidTotp", "198.51.100.7", null, DateTimeOffset.UtcNow),
                            ];
                        }

                        throw new NotSupportedException(method.Name);
                    }));
                });
            });
        }

        public WebApplicationFactory<AppWebModule> Factory { get; }
        public User User { get; } = User.Create("alice", "CurrentPassword!42", "Alice");
        public Guid CredentialId { get; } = Guid.NewGuid();
        public bool TotpEnabled { get; set; }
        public bool TotpValid { get; set; }
        public int TotpValidations { get; private set; }
        public int Enrollments { get; private set; }
        public int Disables { get; private set; }
        public int PasswordChanges { get; private set; }
        public int SignIns { get; private set; }
        public List<string> PasswordIdentifiers { get; } = [];
        public List<string> AuditReasons { get; } = [];

        public HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        public void Dispose() => Factory.Dispose();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        Harness harness) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationSignOutHandler
    {
        // The name claim is deliberately stale (e.g. after a username change) so the
        // page must resolve the current username from the user id.
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, harness.User.Id.ToString()),
                new Claim(ClaimTypes.Name, "stale-username"),
            ], Scheme.Name)), Scheme.Name)));

        public Task SignOutAsync(AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
