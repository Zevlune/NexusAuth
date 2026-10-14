using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Application.Services.OpenApi;
using NexusAuth.Application.Services.Scim;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Persistence.Repositories;
using Npgsql;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

// Run against an isolated database initialized with production-init.sql.
// No default connection is used, so these tests cannot touch an application database accidentally.
public sealed class PostgreSqlTimestampTests
{
    public const string ConnectionVariable = "NEXUSAUTH_TEST_POSTGRES_CONNECTION";

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(8)]
    [InlineData(-5)]
    public async Task Credential_create_update_and_query_accept_non_utc_expiry(int offsetHours)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var unitOfWork = new TestUnitOfWork(context);
        var scimService = new ScimCredentialService(new ScimServicePrincipalCredentialRepository(unitOfWork));
        var openApiService = new OpenApiCredentialService(new OpenApiCredentialRepository(unitOfWork));
        var expiry = Timestamp(offsetHours);
        var name = Guid.NewGuid().ToString("N");

        var scim = await scimService.CreateAsync($"scim-{name}", ["scim:read"], expiry);
        var openApi = await openApiService.CreateAsync($"api-{name}", OpenApiCredential.TargetTypeApplication, expiry);
        AssertUtcInstant(expiry, scim.Credential.ExpiresAt);
        AssertUtcInstant(expiry, openApi.Credential.ExpiresAt);

        var nextExpiry = expiry.AddDays(1);
        await scimService.UpdateAsync(scim.Credential.Id, $"scim-{name}", ["scim:read"], nextExpiry, true);
        await openApiService.UpdateAsync(openApi.Credential.Id, $"api-{name}", nextExpiry, true);
        context.ChangeTracker.Clear();
        var storedScim = await context.ScimServicePrincipalCredentials.SingleAsync(value => value.Id == scim.Credential.Id);
        var storedOpenApi = await context.OpenApiCredentials.SingleAsync(value => value.Id == openApi.Credential.Id);
        AssertUtcInstant(nextExpiry, storedScim.ExpiresAt);
        AssertUtcInstant(nextExpiry, storedOpenApi.ExpiresAt);

        // EF's conversion must also protect direct tracked property writes, independently of entity normalization.
        context.Entry(storedScim).Property(value => value.ExpiresAt).CurrentValue = expiry.AddDays(2);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        storedScim = await context.ScimServicePrincipalCredentials.SingleAsync(value => value.Id == scim.Credential.Id);
        AssertUtcInstant(expiry.AddDays(2), storedScim.ExpiresAt);
        Assert.True(await context.ScimServicePrincipalCredentials.AnyAsync(value => value.Id == storedScim.Id && value.ExpiresAt > expiry));
        Assert.False(await context.ScimServicePrincipalCredentials.AnyAsync(value => value.Id == storedScim.Id && value.ExpiresAt > expiry.AddDays(2)));

        await scimService.UpdateAsync(storedScim.Id, storedScim.Name, ["scim:read"], null, true);
        context.ChangeTracker.Clear();
        Assert.Null((await context.ScimServicePrincipalCredentials.SingleAsync(value => value.Id == storedScim.Id)).ExpiresAt);
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(8)]
    [InlineData(-5)]
    public async Task Raw_sql_lockout_and_totp_updates_preserve_atomic_guards_with_non_utc_times(int offsetHours)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var unitOfWork = new TestUnitOfWork(context);
        var users = new UserRepository(unitOfWork);
        var credentials = new UserCredentialRepository(unitOfWork);
        var now = Timestamp(offsetHours);
        var user = User.Create($"utc-{Guid.NewGuid():N}", "Test-password-only-123!", "UTC test");
        var totp = UserCredential.CreatePendingTotp(user.Id, "test-protected-secret", now.AddMinutes(10), now: now);
        context.Add(user);
        await context.SaveChangesAsync();
        context.Add(totp);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await users.RegisterFailedLoginAsync(user.Id, 2, TimeSpan.FromMinutes(15), now);
        context.ChangeTracker.Clear();
        await users.RegisterFailedLoginAsync(user.Id, 2, TimeSpan.FromMinutes(15), now.AddSeconds(1));
        context.ChangeTracker.Clear();
        var lockedUser = await context.Users.SingleAsync(value => value.Id == user.Id);
        AssertUtcInstant(now.AddSeconds(1).AddMinutes(15), lockedUser.LockedUntil);
        AssertUtcInstant(now.AddSeconds(1), lockedUser.UpdatedAt);
        Assert.Equal(0, lockedUser.FailedLoginAttempts);

        // Requests while locked must leave both the lockout and timestamp intact.
        context.ChangeTracker.Clear();
        await users.RegisterFailedLoginAsync(user.Id, 2, TimeSpan.FromMinutes(15), now.AddSeconds(2));
        context.ChangeTracker.Clear();
        lockedUser = await context.Users.SingleAsync(value => value.Id == user.Id);
        AssertUtcInstant(now.AddSeconds(1).AddMinutes(15), lockedUser.LockedUntil);
        AssertUtcInstant(now.AddSeconds(1), lockedUser.UpdatedAt);

        Assert.True(await credentials.TryConfirmTotpAsync(totp.Id, user.Id, "test-protected-secret", 100, now));
        Assert.False(await credentials.TryConfirmTotpAsync(totp.Id, user.Id, "test-protected-secret", 101, now));
        Assert.False(await credentials.TryUseTotpCounterAsync(totp.Id, user.Id, 100, now.AddSeconds(1)));
        Assert.True(await credentials.TryUseTotpCounterAsync(totp.Id, user.Id, 101, now.AddSeconds(2)));
        context.ChangeTracker.Clear();
        var storedTotp = await context.UserCredentials.SingleAsync(value => value.Id == totp.Id);
        AssertUtcInstant(now.AddSeconds(2), storedTotp.UpdatedAt);
        Assert.Equal(101, storedTotp.LastUsedCounter);
        Assert.Null(storedTotp.PendingExpiresAt);
        Assert.True(await credentials.DisableAsync(totp.Id, user.Id, now.AddSeconds(3)));
        context.ChangeTracker.Clear();
        storedTotp = await context.UserCredentials.SingleAsync(value => value.Id == totp.Id);
        AssertUtcInstant(now.AddSeconds(3), storedTotp.DisabledAt);
        Assert.False(await credentials.TryUseTotpCounterAsync(totp.Id, user.Id, 102, now.AddSeconds(4)));

        await users.ResetLoginFailuresAsync(user.Id, now.AddSeconds(4));
        context.ChangeTracker.Clear();
        var resetUser = await context.Users.SingleAsync(value => value.Id == user.Id);
        Assert.Null(resetUser.LockedUntil);
        AssertUtcInstant(now.AddSeconds(4), resetUser.UpdatedAt);
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(8)]
    [InlineData(-5)]
    public async Task Session_and_device_bulk_updates_convert_non_utc_query_and_setter_parameters(int offsetHours)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var unitOfWork = new TestUnitOfWork(context);
        var sessions = new SsoSessionRepository(unitOfWork);
        var devices = new DeviceAuthorizationRepository(unitOfWork);
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(offsetHours));
        var session = SsoSession.Create(Guid.NewGuid(), TimeSpan.FromHours(1));
        var device = DeviceAuthorization.Create("test-device-client", "openid").Entity;
        context.AddRange(session, device);
        await context.SaveChangesAsync();

        Assert.NotNull(await sessions.FindActiveAsync(session.Id, session.UserId, now));
        await sessions.RevokeAsync(session.Id, session.UserId, now);
        Assert.Null(await sessions.FindActiveAsync(session.Id, session.UserId, now));

        Assert.True(await devices.TryRegisterPollAsync(device.Id, null, now, 5));
        context.ChangeTracker.Clear();
        var storedDevice = await context.DeviceAuthorizations.SingleAsync(value => value.Id == device.Id);
        AssertUtcInstant(now, storedDevice.LastPolledAt);
        // Use the database's microsecond precision with a non-UTC offset as the optimistic comparison parameter.
        var previous = storedDevice.LastPolledAt!.Value.ToOffset(TimeSpan.FromHours(offsetHours));
        Assert.True(await devices.TryRegisterPollAsync(device.Id, previous, now.AddSeconds(5), 5));
        Assert.False(await devices.TryRegisterPollAsync(device.Id, previous, now.AddSeconds(10), 5));
        context.ChangeTracker.Clear();
        AssertUtcInstant(now, (await context.SsoSessions.SingleAsync(value => value.Id == session.Id)).RevokedAt);
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(8)]
    [InlineData(-5)]
    public async Task Authorization_refresh_and_blacklist_queries_accept_non_utc_times(int offsetHours)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var unitOfWork = new TestUnitOfWork(context);
        var codes = new AuthorizationCodeRepository(unitOfWork);
        var tokens = new RefreshTokenRepository(unitOfWork);
        var blacklist = new TokenBlacklistRepository(unitOfWork);
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(offsetHours));
        var userId = Guid.NewGuid();
        var code = AuthorizationCode.Create("timestamp-client", userId, "https://client.example.test/callback", "openid", authenticatedAt: now).Entity;
        var token = RefreshToken.Create("timestamp-client", userId, "openid").Entity;
        var entry = TokenBlacklistEntry.Create(Guid.NewGuid().ToString("N"), "access_token", userId.ToString(), now.AddMinutes(1));
        context.AddRange(code, token, entry);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var consumed = await codes.ConsumeByCodeHashAsync(code.CodeHash, code.ClientId, now);
        Assert.NotNull(consumed);
        AssertUtcInstant(now, consumed.AuthenticatedAt);
        Assert.Null(await codes.ConsumeByCodeHashAsync(code.CodeHash, code.ClientId, now));
        var replacement = RefreshToken.Create(token.ClientId, userId, token.Scope).Entity;
        Assert.NotNull(await tokens.RotateAsync(token.TokenHash, token.ClientId, replacement, now));
        Assert.Null(await tokens.RotateAsync(token.TokenHash, token.ClientId, RefreshToken.Create(token.ClientId, userId, token.Scope).Entity, now));
        Assert.True(await blacklist.ExistsActiveAsync(entry.Jti, now));
        Assert.False(await blacklist.ExistsActiveAsync(entry.Jti, now.AddMinutes(1)));
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(8)]
    [InlineData(-5)]
    public async Task Passkey_and_challenge_lifecycle_persists_non_utc_times(int offsetHours)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var unitOfWork = new TestUnitOfWork(context);
        var credentials = new WebAuthnCredentialRepository(unitOfWork);
        var challenges = new WebAuthnChallengeRepository(unitOfWork);
        var now = Timestamp(offsetHours);
        var user = User.Create($"passkey-utc-{Guid.NewGuid():N}", "Test-password-only-123!", "UTC test");
        context.Add(user);
        await context.SaveChangesAsync();
        var credential = WebAuthnCredential.Create(user.Id, Guid.NewGuid().ToByteArray(), [1, 2, 3], 0, Guid.NewGuid(), ["internal", "usb"], false, false, now: now);
        await credentials.AddAsync(credential);
        var challengeToken = Guid.NewGuid().ToString("N");
        var challenge = WebAuthnChallenge.Create(challengeToken, WebAuthnChallenge.AuthenticationPurpose, user.Id, "{}", null, false, now.AddMinutes(5), now);
        await challenges.AddAsync(challenge);
        context.ChangeTracker.Clear();
        var stored = await context.WebAuthnCredentials.SingleAsync(value => value.Id == credential.Id);
        AssertUtcInstant(now, stored.CreatedAt);
        Assert.Equal(new[] { "internal", "usb" }, stored.Transports);
        stored.RecordAuthentication(1, false, now.AddSeconds(1));
        await credentials.UpdateAsync(stored);
        Assert.NotNull(await challenges.TryConsumeAsync(challengeToken, challenge.Purpose, now.AddSeconds(1)));
        Assert.Null(await challenges.TryConsumeAsync(challengeToken, challenge.Purpose, now.AddSeconds(2)));
        Assert.True(await credentials.DisableAsync(user.Id, credential.Id, now.AddSeconds(2)));
        context.ChangeTracker.Clear();
        stored = await context.WebAuthnCredentials.SingleAsync(value => value.Id == credential.Id);
        AssertUtcInstant(now.AddSeconds(1), stored.LastUsedAt);
        AssertUtcInstant(now.AddSeconds(2), stored.DisabledAt);
        AssertUtcInstant(now.AddSeconds(1), (await context.WebAuthnChallenges.SingleAsync(value => value.Id == challenge.Id)).ConsumedAt);
    }

    private static DateTimeOffset Timestamp(int offsetHours) =>
        new(2030, 1, 2, 12, 34, 56, TimeSpan.FromHours(offsetHours));

    private static void AssertUtcInstant(DateTimeOffset expected, DateTimeOffset? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(TimeSpan.Zero, actual.Value.Offset);
        // PostgreSQL stores timestamps at microsecond precision.
        Assert.Equal(expected.UtcTicks - expected.UtcTicks % TimeSpan.TicksPerMicrosecond, actual.Value.UtcTicks);
    }

    private static AuthDbContext CreateContext()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable)
            ?? throw new InvalidOperationException($"Set {ConnectionVariable} to an isolated test database.");
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.Host is not "localhost" and not "127.0.0.1" ||
            settings.Database?.EndsWith("_utc_regression", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Timestamp regressions require a local database ending in _utc_regression.");
        return new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connectionString).Options);
    }

    private sealed class TestUnitOfWork(AuthDbContext context) : IUnitOfWork
    {
        public ILuckDbContext GetLuckDbContext() => context;
        public Task<int> CommitAsync(CancellationToken ct = default) => context.SaveChangesAsync(ct);
    }
}

public sealed class PostgreSqlTheoryAttribute : TheoryAttribute
{
    public PostgreSqlTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgreSqlTimestampTests.ConnectionVariable)))
            Skip = $"Set {PostgreSqlTimestampTests.ConnectionVariable} to run isolated PostgreSQL regressions.";
    }
}
