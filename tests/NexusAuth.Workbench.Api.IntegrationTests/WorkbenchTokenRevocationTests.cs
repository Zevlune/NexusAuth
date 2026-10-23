using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchTokenRevocationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Administrator_revocation_rejects_existing_bearer_and_allows_new_login(bool gateway)
    {
        using var harness = new Harness(gateway);
        using var administrator = harness.Client(harness.Admin);
        using var target = harness.Client(harness.Target);
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await target.GetAsync("/api/users")).StatusCode);

        var response = await administrator.PostAsync($"/api/users/{harness.Target.Id}/revoke-tokens", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/me")).StatusCode);
        Assert.True(harness.Target.IsActive);
        Assert.Equal(harness.Target.Id, Assert.Single(harness.RefreshRevocations));
        Assert.Equal(harness.Target.Id, Assert.Single(harness.SessionRevocations));
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync("/api/auth/me")).StatusCode);
        using var newLogin = harness.Client(harness.Target);
        Assert.Equal(HttpStatusCode.OK, (await newLogin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("blacklisted")]
    public async Task Existing_bearer_is_rejected_when_persisted_state_changes(string state)
    {
        using var harness = new Harness(true);
        using var target = harness.Client(harness.Target, jti: "target-token");
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/auth/me")).StatusCode);

        if (state == "deleted") harness.Users.Remove(harness.Target.Id);
        else if (state == "blacklisted") harness.Blacklisted.Add("target-token");
        else harness.Target.UpdateScimProfile(harness.Target.Username, harness.Target.Nickname, false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Delete_endpoint_invalidates_existing_bearer_and_prevents_new_login()
    {
        using var harness = new Harness(true);
        using var administrator = harness.Client(harness.Admin);
        using var target = harness.Client(harness.Target);

        Assert.Equal(HttpStatusCode.NoContent, (await administrator.DeleteAsync($"/api/users/{harness.Target.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/me")).StatusCode);
        Assert.False(harness.Users.ContainsKey(harness.Target.Id));
        using var forgedNewLogin = harness.Client(harness.Target);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forgedNewLogin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Reenabling_user_does_not_restore_their_previous_bearer()
    {
        using var harness = new Harness(true);
        using var administrator = harness.Client(harness.Admin);
        using var target = harness.Client(harness.Target);
        using var disable = new StringContent("{\"isActive\":false}", System.Text.Encoding.UTF8, "application/json");
        using var enable = new StringContent("{\"isActive\":true}", System.Text.Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.OK, (await administrator.PatchAsync($"/api/users/{harness.Target.Id}/status", disable)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await administrator.PatchAsync($"/api/users/{harness.Target.Id}/status", enable)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/me")).StatusCode);
        using var newLogin = harness.Client(harness.Target);
        Assert.Equal(HttpStatusCode.OK, (await newLogin.GetAsync("/api/auth/me")).StatusCode);
    }

    private sealed class Harness : IDisposable
    {
        private const string Issuer = "https://issuer.example.test";
        private const string Audience = "nexusauth.workbench.api";
        private const string ClientId = "nexusauth.workbench";
        private readonly RSA rsa = RSA.Create(2048);
        private readonly RsaSecurityKey signingKey;
        private readonly WebApplicationFactory<WorkbenchApiModule> factory;
        public Dictionary<Guid, User> Users { get; } = [];
        public HashSet<string> Blacklisted { get; } = [];
        public List<Guid> RefreshRevocations { get; } = [];
        public List<Guid> SessionRevocations { get; } = [];
        public User Admin { get; } = User.Create("admin", "test-password", "Admin");
        public User Target { get; } = User.Create("target", "test-password", "Target");

        public Harness(bool gateway)
        {
            Admin.MarkAsSystemAccount();
            Users.Add(Admin.Id, Admin);
            Users.Add(Target.Id, Target);
            signingKey = new RsaSecurityKey(rsa) { KeyId = "revocation-test" };
            factory = new WebApplicationFactory<WorkbenchApiModule>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing")
                    .UseSetting("Gateway:Enabled", gateway.ToString())
                    .UseSetting("Auth:Authority", Issuer)
                    .UseSetting("Auth:Audience", Audience)
                    .UseSetting("Auth:ClientId", ClientId)
                    .UseSetting("Auth:ClientSecret", "test-only-secret")
                    .UseSetting("Auth:RedirectUri", "https://dashboard.example.test/signin-oidc")
                    .UseSetting("Auth:PostLogoutRedirectUri", "https://dashboard.example.test/")
                    .UseSetting("Auth:Scope", "openid profile offline_access nexusauth.workbench.api");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton(RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
                    {
                        var id = args?.FirstOrDefault() as Guid?;
                        if (method.Name == nameof(IUserRepository.FindByIdAsync)) return Users.GetValueOrDefault(id!.Value);
                        if (method.Name == nameof(IUserRepository.DeleteAsync)) Users.Remove(((User)args![0]!).Id);
                        return null;
                    }));
                    services.RemoveAll<ITokenBlacklistRepository>();
                    services.AddSingleton(RecordingDispatchProxy.Create<ITokenBlacklistRepository>((method, args) =>
                        method.Name == nameof(ITokenBlacklistRepository.ExistsActiveAsync) && Blacklisted.Contains((string)args![0]!)));
                    services.RemoveAll<IRefreshTokenRepository>();
                    services.AddSingleton(RecordingDispatchProxy.Create<IRefreshTokenRepository>((method, args) =>
                    {
                        if (method.Name == nameof(IRefreshTokenRepository.RevokeAllForUserAsync)) RefreshRevocations.Add((Guid)args![0]!);
                        return null;
                    }));
                    services.RemoveAll<ISsoSessionService>();
                    services.AddSingleton(RecordingDispatchProxy.Create<ISsoSessionService>((method, args) =>
                    {
                        if (method.Name == nameof(ISsoSessionService.RevokeAllForUserAsync)) SessionRevocations.Add((Guid)args![0]!);
                        return null;
                    }));
                    services.PostConfigure<JwtBearerOptions>(WorkbenchAuthenticationDefaults.BearerScheme, options =>
                    {
                        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                        configuration.SigningKeys.Add(signingKey);
                        options.Configuration = configuration;
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    });
                });
            });
        }

        public HttpClient Client(User user, string? jti = null)
        {
            var now = DateTime.UtcNow;
            var claims = new[]
            {
                new Claim("sub", user.Id.ToString()), new Claim("name", user.Nickname),
                new Claim("token_use", "access_token"), new Claim("client_id", ClientId),
                new Claim("jti", jti ?? Guid.NewGuid().ToString()),
                new Claim(UserTokenValidator.EpochClaim, (user.TokenInvalidBefore?.UtcTicks ?? 0).ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
                new Claim("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            };
            var token = new JwtSecurityToken(Issuer, Audience, claims, now.AddSeconds(-1), now.AddMinutes(5),
                new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            return client;
        }

        public void Dispose() { factory.Dispose(); rsa.Dispose(); }
    }
}
