using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Application.Services.LoginAudits;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Host.Authentication;
using Xunit;
using Proxy = NexusAuth.Host.IntegrationTests.PasskeyManagementTests.RecordingProxy;

namespace NexusAuth.Host.IntegrationTests;

public sealed class RequiredMfaEnrollmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Required_totp_starts_enrollment_without_issuing_a_session(bool registering)
    {
        using var harness = new Harness();
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var path = registering ? "/account/register" : "/account/login";
        var html = await client.GetStringAsync(path);
        var values = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["Username"] = "alice",
            ["Password"] = "Correct-password!42",
            ["ConfirmPassword"] = "Correct-password!42",
            ["Nickname"] = "Alice",
            ["Email"] = "alice@example.test",
            ["SliderCaptchaToken"] = "unused-when-disabled",
        };

        var response = await client.PostAsync(path, new FormUrlEncodedContent(values));

        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        Assert.Contains("two-factor-setup", response.Headers.Location!.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.SignIns);
        var setup = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Contains("otpauth://", await setup.Content.ReadAsStringAsync());
        Assert.Contains("no-store", setup.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Setup_requires_an_authenticated_pending_flow()
    {
        using var harness = new Harness();
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/account/two-factor-setup");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("login", response.Headers.Location!.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Enrollments);
        Assert.Equal(0, harness.SignIns);
    }

    [Fact]
    public async Task Setup_completes_only_after_confirmation_and_cannot_be_replayed()
    {
        using var harness = new Harness { ConfirmSucceeds = true };
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/account/login");
        await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(login), ["Username"] = "alice", ["Password"] = "Correct-password!42",
        }));
        var setup = await client.GetStringAsync("/account/two-factor-setup");
        var response = await client.PostAsync("/account/two-factor-setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(setup), ["Code"] = "123456",
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, harness.SignIns);
        Assert.Contains("RECOVERY-CODE-FIXTURE", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/account/two-factor-setup")).StatusCode);
        Assert.Equal(1, harness.SignIns);
    }

    [Fact]
    public async Task Password_reset_invalidates_a_pending_enrollment()
    {
        using var harness = new Harness();
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/account/login");
        await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(login), ["Username"] = "alice", ["Password"] = "Correct-password!42",
        }));
        harness.User.InvalidateTokens(DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/account/two-factor-setup")).StatusCode);
        Assert.Equal(0, harness.SignIns);
    }

    private static string Token(string html) => WebUtility.HtmlDecode(
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

    [Fact]
    public async Task Password_reset_during_confirmation_invalidates_the_pending_enrollment()
    {
        using var harness = new Harness { ConfirmSucceeds = true, InvalidateDuringConfirmation = true };
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/account/login");
        await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(login), ["Username"] = "alice", ["Password"] = "Correct-password!42",
        }));
        var setup = await client.GetStringAsync("/account/two-factor-setup");
        var response = await client.PostAsync("/account/two-factor-setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(setup), ["Code"] = "123456",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("login", response.Headers.Location!.OriginalString);
        Assert.Equal(0, harness.SignIns);
    }

    private sealed class Harness : IDisposable
    {
        private readonly User user = User.Create("alice", "Correct-password!42", "Alice", "alice@example.test");
        public int SignIns { get; private set; }
        public int Enrollments { get; private set; }
        public bool ConfirmSucceeds { get; set; }
        public bool InvalidateDuringConfirmation { get; set; }
        public User User => user;
        private bool enabled;
        public WebApplicationFactory<AppWebModule> Factory { get; }

        public Harness()
        {
            Factory = new WebApplicationFactory<AppWebModule>().WithWebHostBuilder(builder => builder
                .UseEnvironment("Development")
                .UseTestSetting("SelfRegistration:Enabled", "true")
                .UseTestSetting("SliderCaptcha:Enabled", "false")
                .UseTestSetting("WebAuthn:Enabled", "false")
                .UseTestSetting("LoginFlow:Steps:1:Requirement", "required")
                .ConfigureTestServices(services =>
                {
                    services.Remove(services.Single(item => item.ImplementationType == typeof(BootstrapAdminHostedService)));
                    services.AddScoped(_ => Proxy.Create<IUserService>((method, _) => method.Name switch
                    {
                        nameof(IUserService.FindByIdAsync) or nameof(IUserService.ValidateCredentialsAsync) => user,
                        nameof(IUserService.RegisterAsync) => user.Id,
                        _ => throw new NotSupportedException(method.Name),
                    }));
                    services.AddScoped(_ => Proxy.Create<ITotpService>((method, _) => method.Name switch
                    {
                        nameof(ITotpService.IsEnabledAsync) => enabled,
                        nameof(ITotpService.BeginEnrollmentAsync) => Begin(),
                        nameof(ITotpService.FindPendingEnrollmentAsync) => Enrollment(),
                        nameof(ITotpService.ConfirmEnrollmentAsync) => Confirm(),
                        _ => throw new NotSupportedException(method.Name),
                    }));
                    services.AddScoped(_ => Proxy.Create<IWebSignInService>((_, _) => { SignIns++; return null; }));
                    services.AddScoped(_ => Proxy.Create<ILoginAuditService>((_, _) => null));
                    services.AddScoped(_ => Proxy.Create<ISecondFactorRecoveryService>((method, _) => method.Name switch
                    {
                        nameof(ISecondFactorRecoveryService.GenerateCodesAsync) => (IReadOnlyList<string>)["RECOVERY-CODE-FIXTURE"],
                        _ => throw new NotSupportedException(method.Name),
                    }));
                }));
        }

        private TotpEnrollment Begin() { Enrollments++; return Enrollment(); }
        private bool Confirm()
        {
            if (InvalidateDuringConfirmation) user.InvalidateTokens(DateTimeOffset.UtcNow);
            if (ConfirmSucceeds) enabled = true;
            return ConfirmSucceeds;
        }
        private static TotpEnrollment Enrollment() => new(Guid.NewGuid(), "protected-test-secret", "TESTKEY", "otpauth://totp/NexusAuth:alice?secret=TESTKEY");
        public void Dispose() => Factory.Dispose();
    }
}
