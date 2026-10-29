using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class PasskeyAccountTests
{
    [Fact]
    public async Task Page_renders_safe_metadata_and_encodes_user_controlled_names()
    {
        using var harness = new Harness();
        using var client = harness.Factory.CreateClient();
        var html = await client.GetStringAsync("/account/passkeys");
        Assert.Contains("Passkey 管理", html);
        Assert.DoesNotContain("<script>unsafe</script>", html);
        Assert.Contains("&lt;script&gt;unsafe&lt;/script&gt;", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Contains("/account/passkeys?handler=Delete", html);
        Assert.Contains("no-store", (await client.GetAsync("/account/passkeys")).Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Mutations_without_antiforgery_token_are_rejected()
    {
        using var harness = new Harness();
        using var client = harness.Factory.CreateClient();
        var response = await client.PostAsync("/account/passkeys?handler=Rename", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["DisplayName"] = "Laptop", ["CredentialId"] = Guid.NewGuid().ToString() }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, harness.Mutations);
    }

    [Theory]
    [InlineData("Add")]
    [InlineData("Delete")]
    public async Task Incorrect_password_blocks_sensitive_mutations(string handler)
    {
        using var harness = new Harness();
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await GetTokenAsync(client);
        var response = await client.PostAsync($"/account/passkeys?handler={handler}", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["CurrentPassword"] = "incorrect" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, harness.Mutations);
        Assert.DoesNotContain("value=\"incorrect\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enabled_totp_must_be_verified_before_addition()
    {
        using var harness = new Harness { TotpEnabled = true };
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await GetTokenAsync(client);
        var response = await client.PostAsync("/account/passkeys?handler=Add", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["CurrentPassword"] = "correct", ["TotpCode"] = "000000" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, harness.TotpChecks);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Verified_addition_redirects_to_the_existing_enrollment_flow()
    {
        using var harness = new Harness();
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await GetTokenAsync(client);
        var response = await client.PostAsync("/account/passkeys?handler=Add", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["CurrentPassword"] = "correct" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/PasskeyEnrollment?token=", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Rename_uses_session_owner_even_when_request_supplies_another_user_id()
    {
        using var harness = new Harness();
        using var client = harness.Factory.CreateClient();
        var token = await GetTokenAsync(client);
        var response = await client.PostAsync("/account/passkeys?handler=Rename", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["DisplayName"] = "Laptop",
                ["CredentialId"] = Guid.NewGuid().ToString(), ["UserId"] = Guid.NewGuid().ToString() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(harness.User.Id, harness.MutationOwner);
    }

    [Fact]
    public async Task Verified_deletion_revokes_the_owned_credential_and_signs_out()
    {
        using var harness = new Harness { RevokeSucceeds = true };
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await GetTokenAsync(client);
        var response = await client.PostAsync("/account/passkeys?handler=Delete", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["CurrentPassword"] = "correct",
                ["CredentialId"] = Guid.NewGuid().ToString() }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(harness.User.Id, harness.MutationOwner);
        Assert.Equal(1, harness.SignOuts);
        Assert.StartsWith("/Account/Login?", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Last_passkey_cannot_be_deleted_when_required_totp_is_not_configured()
    {
        using var harness = new Harness(requireTotp: true) { RevokeSucceeds = true };
        using var client = harness.Factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await GetTokenAsync(client);
        var response = await client.PostAsync("/account/passkeys?handler=Delete", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["CurrentPassword"] = "correct",
                ["CredentialId"] = harness.CredentialId.ToString() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, harness.Mutations);
        Assert.Equal(0, harness.SignOuts);
    }

    private static async Task<string> GetTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/account/passkeys");
        return WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    private sealed class Harness : IDisposable
    {
        public User User { get; } = User.Create("alice", "test-password", "Alice");
        public Guid CredentialId { get; } = Guid.NewGuid();
        public bool TotpEnabled { get; set; }
        public int TotpChecks { get; private set; }
        public int Mutations { get; private set; }
        public bool RevokeSucceeds { get; set; }
        public int SignOuts { get; set; }
        public Guid? MutationOwner { get; private set; }
        public WebApplicationFactory<AppWebModule> Factory { get; }

        public Harness(bool requireTotp = false)
        {
            Factory = new WebApplicationFactory<AppWebModule>().WithWebHostBuilder(builder => builder.UseEnvironment("Development")
                .UseTestSetting("WebAuthn:Enabled", "true")
                .UseTestSetting("LoginFlow:Steps:1:Requirement", requireTotp ? "required" : "conditional")
                .ConfigureTestServices(services =>
                {
                    services.Remove(services.Single(item => item.ImplementationType == typeof(BootstrapAdminHostedService)));
                    services.PostConfigure<AuthenticationOptions>(options =>
                        options.SchemeMap[AppWebModule.AuthenticationScheme].HandlerType = typeof(TestAuthenticationHandler));
                    services.AddSingleton(this);
                    services.AddTransient<TestAuthenticationHandler>();
                    services.AddScoped(_ => PasskeyManagementTests.RecordingProxy.Create<IUserService>((method, args) => method.Name switch
                    {
                        nameof(IUserService.FindByIdAsync) => User,
                        nameof(IUserService.ValidateCredentialsAsync) => (string)args![1]! == "correct" ? User : null,
                        _ => throw new NotSupportedException(method.Name),
                    }));
                    services.AddScoped(_ => PasskeyManagementTests.RecordingProxy.Create<ITotpService>((method, _) =>
                    {
                        if (method.Name == nameof(ITotpService.IsEnabledAsync)) return TotpEnabled;
                        if (method.Name == nameof(ITotpService.ValidateAsync)) { TotpChecks++; return false; }
                        throw new NotSupportedException(method.Name);
                    }));
                    services.AddScoped(_ => PasskeyManagementTests.RecordingProxy.Create<IPasskeyManagementService>((method, args) =>
                    {
                        if (method.Name == nameof(IPasskeyManagementService.GetAsync))
                            return (IReadOnlyList<WebAuthnCredentialSummary>)new[] { new WebAuthnCredentialSummary(CredentialId,
                                "<script>unsafe</script>", DateTimeOffset.UtcNow, null, null, true, true) };
                        Mutations++;
                        MutationOwner = (Guid)args![0]!;
                        return method.Name == nameof(IPasskeyManagementService.RevokeAsync) && RevokeSucceeds;
                    }));
                }));
        }

        public void Dispose() => Factory.Dispose();
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, Harness harness) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationSignOutHandler
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, harness.User.Id.ToString()), new Claim(ClaimTypes.Name, harness.User.Username)
            ], Scheme.Name)), Scheme.Name)));

        public Task SignOutAsync(AuthenticationProperties? properties)
        {
            harness.SignOuts++;
            return Task.CompletedTask;
        }
    }
}
