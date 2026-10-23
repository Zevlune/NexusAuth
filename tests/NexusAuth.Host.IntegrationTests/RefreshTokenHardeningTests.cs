using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.Users;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class RefreshTokenHardeningTests : IClassFixture<RefreshTokenHardeningTests.SharedUser>
{
    private readonly User user;

    public RefreshTokenHardeningTests(SharedUser shared)
    {
        user = shared.User;
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_users_tokens_for_that_client()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var original = await harness.Service.IssueRefreshTokenAsync("public-app", user.Id, "openid offline_access");
        var otherClient = await harness.Service.IssueRefreshTokenAsync("other-app", user.Id, "openid offline_access");

        var rotated = await harness.Service.RefreshAsync(original, "public-app");
        Assert.True(rotated.IsSuccess);
        var descendant = rotated.RefreshToken!;

        // An attacker (or a stale copy) replays the already rotated token.
        var replay = await harness.Service.RefreshAsync(original, "public-app");

        Assert.False(replay.IsSuccess);
        Assert.Equal(1, harness.FamilyRevocations);
        Assert.True(harness.IsRefreshTokenRevoked(descendant));
        Assert.False((await harness.Service.RefreshAsync(descendant, "public-app")).IsSuccess);
        // Tokens of the same user issued to another client are not affected.
        Assert.False(harness.IsRefreshTokenRevoked(otherClient));
    }

    [Fact]
    public async Task Presenting_an_explicitly_revoked_refresh_token_does_not_revoke_other_sessions()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var revoked = await harness.Service.IssueRefreshTokenAsync("web-app", user.Id, "openid offline_access");
        var otherSession = await harness.Service.IssueRefreshTokenAsync("web-app", user.Id, "openid offline_access");
        await harness.Service.RevokeRefreshTokenAsync(revoked, "web-app");

        var result = await harness.Service.RefreshAsync(revoked, "web-app");

        Assert.False(result.IsSuccess);
        Assert.Equal(0, harness.FamilyRevocations);
        Assert.True((await harness.Service.RefreshAsync(otherSession, "web-app")).IsSuccess);
    }

    [Fact]
    public async Task Access_token_failure_does_not_consume_the_refresh_token()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        harness.AddResource(ApiResource.Create("orders", "Orders", "orders.api"));
        harness.AddResource(ApiResource.Create("billing", "Billing", "billing.api"));
        // A legacy token whose stored scope spans two audiences cannot produce an access token.
        var raw = await harness.Service.IssueRefreshTokenAsync("legacy-app", user.Id, "orders.api billing.api");

        var result = await harness.Service.RefreshAsync(raw, "legacy-app");

        Assert.False(result.IsSuccess);
        Assert.Equal("Unable to issue an access token for this refresh token.", result.Error);
        Assert.False(harness.IsRefreshTokenRevoked(raw));
    }

    [Fact]
    public async Task Issue_access_token_surfaces_the_original_exception_type()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.AddResource(ApiResource.Create("orders", "Orders", "orders.api"));
        harness.AddResource(ApiResource.Create("billing", "Billing", "billing.api"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.IssueAccessTokenAsync("machine", "orders.api billing.api"));
    }

    [Fact]
    public async Task User_rejected_by_security_policy_cannot_refresh()
    {
        var policy = new TestSecurityPolicy();
        using var harness = new InMemoryTokenServiceHarness(policy);
        harness.Add(user);
        var raw = await harness.Service.IssueRefreshTokenAsync("web-app", user.Id, "openid offline_access");
        policy.BlockedUsers.Add(user.Id);

        var result = await harness.Service.RefreshAsync(raw, "web-app");

        Assert.False(result.IsSuccess);
        Assert.False(harness.IsRefreshTokenRevoked(raw));
    }

    [Fact]
    public async Task Introspection_reports_id_tokens_as_inactive()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync("web-app", "openid profile", null, user.Id);
        var idToken = await harness.Service.IssueIdTokenAsync("web-app", user.Id, null, accessToken);

        Assert.True((await harness.Service.IntrospectAsync(accessToken, "web-app")).Active);
        Assert.False((await harness.Service.IntrospectAsync(idToken, "web-app")).Active);
        Assert.False((await harness.Service.IntrospectAsync(idToken)).Active);
    }

    public sealed class SharedUser
    {
        public User User { get; } = User.Create($"refresh-{Guid.NewGuid():N}", "Test-password-123!", "Refresh User");
    }
}
