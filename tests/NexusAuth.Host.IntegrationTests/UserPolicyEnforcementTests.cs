using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.DeviceAuthorization;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// User security policy (black/white lists) must be enforced before a WeChat user is provisioned and
/// whenever an already authenticated user approves a device.
/// </summary>
public sealed class UserPolicyEnforcementTests
{
    [Fact]
    public async Task WeChat_login_checks_the_policy_before_creating_a_user()
    {
        var policy = TestSecurityPolicy.Whitelist("existing-admin");
        var harness = new WeChatHarness(policy);

        var exception = await Assert.ThrowsAsync<WeChatMiniProgramLoginException>(() => harness.Service.AuthenticateAsync("code-1"));

        Assert.Equal("invalid_grant", exception.ErrorCode);
        Assert.Equal(1, policy.UserChecks);
        Assert.Equal(0, harness.CreatedUsers);
    }

    [Fact]
    public async Task WeChat_login_without_auto_provisioning_does_not_create_users()
    {
        var harness = new WeChatHarness(new TestSecurityPolicy(), autoProvisionUsers: false);

        var exception = await Assert.ThrowsAsync<WeChatMiniProgramLoginException>(() => harness.Service.AuthenticateAsync("code-1"));

        Assert.Equal("invalid_grant", exception.ErrorCode);
        Assert.Equal(0, harness.CreatedUsers);
    }

    [Fact]
    public async Task WeChat_login_provisions_users_allowed_by_policy()
    {
        var harness = new WeChatHarness(new TestSecurityPolicy());

        var result = await harness.Service.AuthenticateAsync("code-1");

        Assert.StartsWith("wx_", result.User.Username);
        Assert.Equal(1, harness.CreatedUsers);
    }

    [Fact]
    public async Task Device_approval_is_rejected_for_a_policy_blocked_user()
    {
        var user = User.Create($"device-{Guid.NewGuid():N}", "Test-password-123!", "Device User");
        var policy = new TestSecurityPolicy();
        var authorization = DeviceAuthorization.Create("tv-client", "openid").Entity;
        var approvals = 0;
        var devices = HardeningProxy.Create<IDeviceAuthorizationRepository>((method, _) =>
        {
            switch (method.Name)
            {
                case nameof(IDeviceAuthorizationRepository.FindByUserCodeAsync):
                    return authorization;
                case nameof(IDeviceAuthorizationRepository.ApprovePendingAsync):
                    approvals++;
                    return true;
                default:
                    return null;
            }
        });
        var users = HardeningProxy.Create<IUserRepository>((method, args) =>
            method.Name == nameof(IUserRepository.FindByIdAsync) && (Guid)args![0]! == user.Id ? user : null);
        var service = new DeviceAuthorizationService(
            HardeningProxy.Create<IClientService>((_, _) => null),
            devices,
            users,
            policy.Service,
            Options.Create(new JwtOptions()));

        policy.BlockedUsers.Add(user.Id);
        var blocked = await service.ApproveAsync(authorization.UserCode, user.Id);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(0, approvals);

        policy.BlockedUsers.Clear();
        var allowed = await service.ApproveAsync(authorization.UserCode, user.Id);
        Assert.True(allowed.IsSuccess);
        Assert.Equal(1, approvals);
    }

    private sealed class WeChatHarness
    {
        public WeChatHarness(TestSecurityPolicy policy, bool autoProvisionUsers = true)
        {
            var httpClient = new HttpClient(new OpenIdHandler());
            var factory = HardeningProxy.Create<IHttpClientFactory>((method, _) =>
                method.Name == nameof(IHttpClientFactory.CreateClient) ? httpClient : null);
            var identities = HardeningProxy.Create<IUserExternalIdentityRepository>((method, args) =>
            {
                if (method.Name != nameof(IUserExternalIdentityRepository.GetOrCreateUserAsync))
                    return null;
                CreatedUsers++;
                return (User)args![0]!;
            });
            Service = new WeChatMiniProgramLoginService(
                factory,
                HardeningProxy.Create<IUserRepository>((_, _) => null),
                identities,
                Options.Create(new WeChatMiniProgramOptions
                {
                    Enabled = true,
                    AppId = "wx-app",
                    AppSecret = "wx-secret",
                    AutoProvisionUsers = autoProvisionUsers,
                }),
                NullLogger<WeChatMiniProgramLoginService>.Instance,
                policy.Service);
        }

        public WeChatMiniProgramLoginService Service { get; }

        public int CreatedUsers { get; private set; }
    }

    private sealed class OpenIdHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"openid\":\"openid-{Guid.NewGuid():N}\",\"session_key\":\"server-only\"}}", Encoding.UTF8, "application/json"),
            });
    }
}
