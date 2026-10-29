using System.IdentityModel.Tokens.Jwt;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class RefreshTokenHardeningTests : IClassFixture<RefreshTokenHardeningTests.SharedUser>
{
    private const string RefreshScope = $"{OidcScopes.OpenId} {OidcScopes.OfflineAccess}";
    private const string ProfileScope = $"{OidcScopes.OpenId} {OidcScopes.Profile}";
    private const string ProfileRefreshScope = $"{OidcScopes.OpenId} {OidcScopes.Profile} {OidcScopes.OfflineAccess}";
    private const string EmailRefreshScope = $"{OidcScopes.OpenId} {OidcScopes.Email} {OidcScopes.OfflineAccess}";
    private const string FullRefreshScope =
        $"{OidcScopes.OpenId} {OidcScopes.Profile} {OidcScopes.Email} {OidcScopes.OfflineAccess}";

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
        var original = await harness.Service.IssueRefreshTokenAsync("public-app", user.Id, RefreshScope);
        var otherClient = await harness.Service.IssueRefreshTokenAsync("other-app", user.Id, RefreshScope);

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
        var revoked = await harness.Service.IssueRefreshTokenAsync("web-app", user.Id, RefreshScope);
        var otherSession = await harness.Service.IssueRefreshTokenAsync("web-app", user.Id, RefreshScope);
        await harness.Service.RevokeRefreshTokenAsync(revoked, "web-app");

        var result = await harness.Service.RefreshAsync(revoked, "web-app");

        Assert.False(result.IsSuccess);
        Assert.Equal(0, harness.FamilyRevocations);
        Assert.True((await harness.Service.RefreshAsync(otherSession, "web-app")).IsSuccess);
    }

    [Fact]
    public async Task Invalid_current_scope_does_not_consume_the_refresh_token()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        harness.AddResource(ApiResource.Create("orders", "Orders", "orders.api"));
        harness.AddResource(ApiResource.Create("billing", "Billing", "billing.api"));
        // A legacy token whose stored scope spans two audiences cannot produce an access token.
        var raw = await harness.Service.IssueRefreshTokenAsync("legacy-app", user.Id, "orders.api billing.api");

        var result = await harness.Service.RefreshAsync(raw, "legacy-app");

        Assert.False(result.IsSuccess);
        Assert.Equal("Requested scopes span multiple audiences. Please request one resource audience per token.", result.Error);
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
        var raw = await harness.Service.IssueRefreshTokenAsync("web-app", user.Id, RefreshScope);
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
        var accessToken = await harness.Service.IssueAccessTokenAsync("web-app", ProfileScope, null, user.Id);
        var idToken = await harness.Service.IssueIdTokenAsync("web-app", user.Id, null, accessToken);

        Assert.True((await harness.Service.IntrospectAsync(accessToken, "web-app")).Active);
        Assert.False((await harness.Service.IntrospectAsync(idToken, "web-app")).Active);
        Assert.False((await harness.Service.IntrospectAsync(idToken)).Active);
    }

    [Fact]
    public async Task Wrong_client_rotated_replay_does_not_revoke_the_real_client_session()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var original = await harness.Service.IssueRefreshTokenAsync("web-app", user.Id, RefreshScope);
        var rotated = await harness.Service.RefreshAsync(original, "web-app");
        Assert.True(rotated.IsSuccess);

        var attackerReplay = await harness.Service.RefreshAsync(original, "other-client");

        Assert.False(attackerReplay.IsSuccess);
        Assert.Equal("Refresh token does not belong to the authenticated client.", attackerReplay.Error);
        Assert.Equal("invalid_grant", attackerReplay.ErrorCode);
        Assert.Equal(0, harness.FamilyRevocations);
        Assert.False(harness.IsRefreshTokenRevoked(rotated.RefreshToken!));
        Assert.True((await harness.Service.RefreshAsync(rotated.RefreshToken!, "web-app")).IsSuccess);
    }

    [Fact]
    public async Task Expanded_scope_is_rejected_without_consuming_the_refresh_token()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var original = await harness.Service.IssueRefreshTokenAsync(
            "web-app",
            user.Id,
            ProfileRefreshScope);

        var result = await harness.Service.RefreshAsync(
            original,
            "web-app",
            scope: FullRefreshScope);

        Assert.False(result.IsSuccess);
        Assert.Equal("scope exceeds the original refresh token grant.", result.Error);
        Assert.Equal("invalid_scope", result.ErrorCode);
        Assert.False(harness.IsRefreshTokenRevoked(original));
    }

    [Fact]
    public async Task Blank_scope_is_rejected_as_invalid_scope_without_consuming_the_token()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var original = await harness.Service.IssueRefreshTokenAsync(
            "web-app",
            user.Id,
            RefreshScope);

        var result = await harness.Service.RefreshAsync(original, "web-app", scope: "   ");

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_scope", result.ErrorCode);
        Assert.False(harness.IsRefreshTokenRevoked(original));
    }

    [Theory]
    [InlineData(OidcScopes.OpenId + "\t" + OidcScopes.Profile)]
    [InlineData(OidcScopes.OpenId + "\r\n" + OidcScopes.Profile)]
    public async Task Invalid_scope_syntax_is_rejected_before_normalization(string invalidScope)
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var original = await harness.Service.IssueRefreshTokenAsync(
            "web-app",
            user.Id,
            ProfileRefreshScope);

        var result = await harness.Service.RefreshAsync(original, "web-app", scope: invalidScope);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_scope", result.ErrorCode);
        Assert.False(harness.IsRefreshTokenRevoked(original));
    }

    [Fact]
    public async Task Narrowed_scope_limits_access_token_but_rotated_refresh_keeps_original_grant()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var original = await harness.Service.IssueRefreshTokenAsync("web-app", user.Id, FullRefreshScope);

        var narrowed = await harness.Service.RefreshAsync(
            original,
            "web-app",
            scope: RefreshScope);

        Assert.True(narrowed.IsSuccess);
        Assert.Equal(RefreshScope, narrowed.Scope);
        Assert.Equal(
            RefreshScope,
            new JwtSecurityTokenHandler().ReadJwtToken(narrowed.AccessToken).Claims
                .Single(claim => claim.Type == "scope").Value);

        var nextRefresh = await harness.Service.RefreshAsync(narrowed.RefreshToken!, "web-app");
        Assert.True(nextRefresh.IsSuccess);
        Assert.Equal(FullRefreshScope, nextRefresh.Scope);
    }

    [Fact]
    public async Task Removed_client_scope_rejects_refresh_without_consuming_the_token()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var original = await harness.Service.IssueRefreshTokenAsync(
            "web-app",
            user.Id,
            EmailRefreshScope);
        harness.RevokeClientScope(OidcScopes.Email);

        var result = await harness.Service.RefreshAsync(original, "web-app");

        Assert.False(result.IsSuccess);
        Assert.Equal("Scope 'email' is no longer allowed.", result.Error);
        Assert.Equal("invalid_grant", result.ErrorCode);
        Assert.False(harness.IsRefreshTokenRevoked(original));
    }

    [Fact]
    public async Task Inactive_resource_rejects_refresh_without_consuming_the_token()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var resource = ApiResource.Create("orders", "Orders", "orders.api");
        harness.AddResource(resource);
        var original = await harness.Service.IssueRefreshTokenAsync(
            "web-app",
            user.Id,
            $"orders.api {OidcScopes.OfflineAccess}");
        resource.Update(isActive: false);

        var result = await harness.Service.RefreshAsync(original, "web-app");

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Scope 'orders.api' does not correspond to an active resource.",
            result.Error);
        Assert.Equal("invalid_grant", result.ErrorCode);
        Assert.False(harness.IsRefreshTokenRevoked(original));
    }

    [Fact]
    public async Task Introspection_is_inactive_after_user_is_blocked_by_policy()
    {
        var policy = new TestSecurityPolicy();
        using var harness = new InMemoryTokenServiceHarness(policy);
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync(
            "web-app",
            ProfileScope,
            userId: user.Id);
        policy.BlockedUsers.Add(user.Id);

        Assert.False((await harness.Service.IntrospectAsync(accessToken, "web-app")).Active);
    }

    public sealed class SharedUser
    {
        public User User { get; } = User.Create($"refresh-{Guid.NewGuid():N}", "Test-password-123!", "Refresh User");
    }
}
