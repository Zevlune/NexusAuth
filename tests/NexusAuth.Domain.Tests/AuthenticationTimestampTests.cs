using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using Xunit;

namespace NexusAuth.Domain.Tests;

/// <summary>
/// A +08:00 offset and negative offsets must all be persisted as UTC (Offset == zero)
/// while preserving the represented instant. Lifecycle methods that default to
/// <see cref="DateTimeOffset.UtcNow"/> must keep doing so.
/// </summary>
public sealed class AuthenticationTimestampTests
{
    public static IEnumerable<object[]> NonUtcOffsets =>
    [
        [TimeSpan.FromHours(8)],
        [TimeSpan.FromHours(-5)],
        [new TimeSpan(-3, -30, 0)],
    ];

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void User_InvalidateTokens_normalizes_the_cutoff_and_updated_at(TimeSpan offset)
    {
        var user = User.Create("alice", "OldPassword!1", "Alice");
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        user.InvalidateTokens(provided);

        AssertUtcInstant(provided, user.TokenInvalidBefore);
        AssertUtcInstant(provided, user.UpdatedAt);
    }

    [Fact]
    public void User_InvalidateTokens_preserves_subsecond_precision()
    {
        var user = User.Create("alice", "OldPassword!1", "Alice");
        var provided = new DateTimeOffset(2026, 5, 6, 15, 8, 9, 500, TimeSpan.FromHours(8));

        user.InvalidateTokens(provided);

        var expectedCutoff = provided.ToUniversalTime();
        Assert.Equal(TimeSpan.Zero, user.TokenInvalidBefore!.Value.Offset);
        Assert.Equal(expectedCutoff.UtcDateTime, user.TokenInvalidBefore!.Value.UtcDateTime);
        // Keep the cutoff precise enough to revoke tokens issued in the same second.
        Assert.Equal(expectedCutoff.UtcDateTime, provided.UtcDateTime);
        Assert.Equal(provided.UtcDateTime, user.UpdatedAt.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, user.UpdatedAt.Offset);
    }

    [Fact]
    public void User_InvalidateTokens_advances_the_revision_even_when_the_clock_does_not_advance()
    {
        var user = User.Create("alice", "OldPassword!1", "Alice");
        var instant = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        user.InvalidateTokens(instant);
        var first = user.TokenInvalidBefore!.Value;
        Assert.Equal(0, first.UtcTicks % 10);

        user.InvalidateTokens(instant);
        Assert.Equal(first.AddTicks(10), user.TokenInvalidBefore);
        user.InvalidateTokens(instant.AddSeconds(-1));
        Assert.Equal(first.AddTicks(20), user.TokenInvalidBefore);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void User_RegisterFailedLogin_normalizes_locked_until_and_updated_at(TimeSpan offset)
    {
        var user = User.Create("alice", "OldPassword!1", "Alice");
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);
        var lockout = TimeSpan.FromMinutes(15);

        user.RegisterFailedLogin(provided, failureLimit: 1, lockoutDuration: lockout);

        Assert.NotNull(user.LockedUntil);
        Assert.Equal(TimeSpan.Zero, user.LockedUntil!.Value.Offset);
        Assert.Equal(provided.Add(lockout).UtcDateTime, user.LockedUntil!.Value.UtcDateTime);
        AssertUtcInstant(provided, user.UpdatedAt);

        // Instant-based lockout boundary is unchanged: locked strictly before the expiry.
        Assert.True(user.IsLoginLocked(provided));
        Assert.False(user.IsLoginLocked(provided.Add(lockout)));
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void User_RegisterSuccessfulLogin_normalizes_updated_at(TimeSpan offset)
    {
        var user = User.Create("alice", "OldPassword!1", "Alice");
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        user.RegisterFailedLogin(provided, failureLimit: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        user.RegisterSuccessfulLogin(provided);

        AssertUtcInstant(provided, user.UpdatedAt);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
    }

    [Fact]
    public void User_default_times_are_utc()
    {
        var before = DateTimeOffset.UtcNow;

        var user = User.Create("alice", "OldPassword!1", "Alice");

        var after = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.Zero, user.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, user.UpdatedAt.Offset);
        Assert.Null(user.TokenInvalidBefore);
        Assert.Null(user.LockedUntil);
        Assert.InRange(user.CreatedAt.UtcDateTime, before.UtcDateTime, after.UtcDateTime);
        Assert.InRange(user.UpdatedAt.UtcDateTime, before.UtcDateTime, after.UtcDateTime);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void AuthorizationCode_normalizes_authenticated_at(TimeSpan offset)
    {
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        var result = AuthorizationCode.Create(
            "client",
            Guid.NewGuid(),
            "https://client.example/callback",
            "openid",
            authenticatedAt: provided);

        AssertUtcInstant(provided, result.Entity.AuthenticatedAt);
        Assert.Equal(TimeSpan.Zero, result.Entity.ExpiresAt.Offset);
        Assert.Equal(TimeSpan.Zero, result.Entity.CreatedAt.Offset);
    }

    [Fact]
    public void AuthorizationCode_keeps_null_authenticated_at()
    {
        var result = AuthorizationCode.Create(
            "client",
            Guid.NewGuid(),
            "https://client.example/callback",
            "openid");

        Assert.Null(result.Entity.AuthenticatedAt);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void AuthorizationCode_creation_expiry_uses_utc_now(TimeSpan offset)
    {
        // The offset is irrelevant for creation; the expiry is derived from UtcNow.
        _ = offset;
        var before = DateTimeOffset.UtcNow;

        var result = AuthorizationCode.Create(
            "client",
            Guid.NewGuid(),
            "https://client.example/callback",
            "openid");

        var after = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.Zero, result.Entity.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, result.Entity.ExpiresAt.Offset);
        Assert.InRange(result.Entity.CreatedAt.UtcDateTime, before.UtcDateTime, after.UtcDateTime);
        Assert.Equal(
            result.Entity.CreatedAt.AddMinutes(10).UtcDateTime,
            result.Entity.ExpiresAt.UtcDateTime);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void DeviceAuthorization_RegisterPoll_normalizes_last_polled_at(TimeSpan offset)
    {
        var authorization = DeviceAuthorization.Create("client", "openid").Entity;
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        authorization.RegisterPoll(provided, slowDown: false);

        AssertUtcInstant(provided, authorization.LastPolledAt);
    }

    [Fact]
    public void DeviceAuthorization_slow_down_boundary_is_unchanged()
    {
        var authorization = DeviceAuthorization.Create("client", "openid", intervalSeconds: 5).Entity;
        var polledAt = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(8));

        authorization.RegisterPoll(polledAt, slowDown: false);

        Assert.True(authorization.RequiresSlowDown(polledAt.AddSeconds(4)));
        Assert.False(authorization.RequiresSlowDown(polledAt.AddSeconds(5)));
        Assert.False(authorization.RequiresSlowDown(polledAt.AddSeconds(6)));
    }

    [Fact]
    public void DeviceAuthorization_default_times_are_utc()
    {
        var before = DateTimeOffset.UtcNow;

        var authorization = DeviceAuthorization.Create("client", "openid").Entity;
        authorization.RegisterPoll(DateTimeOffset.UtcNow, slowDown: false);
        authorization.Approve(Guid.NewGuid());

        var after = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.Zero, authorization.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, authorization.ExpiresAt.Offset);
        Assert.Equal(TimeSpan.Zero, authorization.LastPolledAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, authorization.AuthorizedAt!.Value.Offset);
        Assert.InRange(authorization.AuthorizedAt!.Value.UtcDateTime, before.UtcDateTime, after.UtcDateTime);
    }

    [Fact]
    public void DeviceAuthorization_Deny_default_time_is_utc()
    {
        var authorization = DeviceAuthorization.Create("client", "openid").Entity;

        authorization.Deny();

        Assert.Equal(TimeSpan.Zero, authorization.AuthorizedAt!.Value.Offset);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void SsoSession_Revoke_normalizes_revoked_at(TimeSpan offset)
    {
        var session = SsoSession.Create(Guid.NewGuid(), TimeSpan.FromHours(1));
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        session.Revoke(provided);

        AssertUtcInstant(provided, session.RevokedAt);
    }

    [Fact]
    public void SsoSession_Revoke_is_idempotent_and_keeps_first_time()
    {
        var session = SsoSession.Create(Guid.NewGuid(), TimeSpan.FromHours(1));
        var first = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(8));
        var second = first.AddHours(5);

        session.Revoke(first);
        var recorded = session.RevokedAt;
        session.Revoke(second);

        Assert.Equal(recorded, session.RevokedAt);
        AssertUtcInstant(first, session.RevokedAt);
    }

    [Fact]
    public void SsoSession_default_times_are_utc_and_active_until_expiry()
    {
        var before = DateTimeOffset.UtcNow;

        var session = SsoSession.Create(Guid.NewGuid(), TimeSpan.FromMinutes(30));

        var after = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.Zero, session.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, session.ExpiresAt.Offset);
        Assert.InRange(session.CreatedAt.UtcDateTime, before.UtcDateTime, after.UtcDateTime);
        Assert.True(session.IsActive(session.ExpiresAt.AddTicks(-1)));
        Assert.False(session.IsActive(session.ExpiresAt));
        Assert.Null(session.RevokedAt);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void TokenBlacklistEntry_normalizes_expires_at(TimeSpan offset)
    {
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        var entry = TokenBlacklistEntry.Create("jti-1", "access_token", "subject-1", provided);

        AssertUtcInstant(provided, entry.ExpiresAt);
        Assert.Equal(TimeSpan.Zero, entry.RevokedAt.Offset);
    }

    private static void AssertUtcInstant(DateTimeOffset expected, DateTimeOffset? actual)
    {
        Assert.NotNull(actual);
        var value = actual!.Value;
        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(expected.UtcDateTime, value.UtcDateTime);
        Assert.Equal(expected, value);
    }
}
