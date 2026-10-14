using NexusAuth.Domain.Entities;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class ScimServicePrincipalCredentialTests
{
    // +08:00 and negative offsets must all be normalized to UTC (Offset == zero)
    // while preserving the represented instant.
    public static IEnumerable<object[]> NonUtcOffsets =>
    [
        [TimeSpan.FromHours(8)],
        [TimeSpan.FromHours(-5)],
        [new TimeSpan(-3, -30, 0)],
    ];

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void Create_normalizes_expiresAt_to_utc(TimeSpan offset)
    {
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        var result = ScimServicePrincipalCredential.Create("scim-client", expiresAt: provided);

        AssertUtcInstant(provided, result.Entity.ExpiresAt);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void CreateWithToken_normalizes_expiresAt_to_utc(TimeSpan offset)
    {
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        var credential = ScimServicePrincipalCredential.CreateWithToken(
            "scim-client",
            "raw-token",
            expiresAt: provided);

        AssertUtcInstant(provided, credential.ExpiresAt);
    }

    [Fact]
    public void Create_keeps_null_expiresAt()
    {
        var result = ScimServicePrincipalCredential.Create("scim-client");

        Assert.Null(result.Entity.ExpiresAt);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void Update_normalizes_expiresAt_to_utc(TimeSpan offset)
    {
        var credential = ScimServicePrincipalCredential.CreateWithToken("scim-client", "raw-token");
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        credential.Update("scim-client-renamed", scopes: null, expiresAt: provided);

        AssertUtcInstant(provided, credential.ExpiresAt);
    }

    [Fact]
    public void Update_with_null_clears_expiresAt()
    {
        var credential = ScimServicePrincipalCredential.CreateWithToken(
            "scim-client",
            "raw-token",
            expiresAt: new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(8)));

        credential.Update("scim-client", scopes: null, expiresAt: null);

        Assert.Null(credential.ExpiresAt);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void RecordUse_normalizes_explicit_time_to_utc(TimeSpan offset)
    {
        var credential = ScimServicePrincipalCredential.CreateWithToken("scim-client", "raw-token");
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        credential.RecordUse(provided);

        AssertUtcInstant(provided, credential.LastUsedAt);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void Revoke_normalizes_explicit_time_to_utc(TimeSpan offset)
    {
        var credential = ScimServicePrincipalCredential.CreateWithToken("scim-client", "raw-token");
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);

        credential.Revoke(provided);

        AssertUtcInstant(provided, credential.RevokedAt);
    }

    [Fact]
    public void Revoke_is_idempotent_and_keeps_first_time()
    {
        var credential = ScimServicePrincipalCredential.CreateWithToken("scim-client", "raw-token");
        var first = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(8));
        var second = first.AddHours(5);

        credential.Revoke(first);
        var recorded = credential.RevokedAt;
        credential.Revoke(second);

        Assert.Equal(recorded, credential.RevokedAt);
        AssertUtcInstant(first, credential.RevokedAt);
    }

    [Fact]
    public void Default_times_are_utc()
    {
        var before = DateTimeOffset.UtcNow;

        var credential = ScimServicePrincipalCredential.CreateWithToken("scim-client", "raw-token");
        credential.RecordUse();
        credential.Revoke();

        var after = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.Zero, credential.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, credential.LastUsedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, credential.RevokedAt!.Value.Offset);

        Assert.InRange(credential.CreatedAt.UtcDateTime, before.UtcDateTime, after.UtcDateTime);
        Assert.InRange(credential.LastUsedAt!.Value.UtcDateTime, before.UtcDateTime, after.UtcDateTime);
        Assert.InRange(credential.RevokedAt!.Value.UtcDateTime, before.UtcDateTime, after.UtcDateTime);
    }

    [Theory]
    [MemberData(nameof(NonUtcOffsets))]
    public void Expiration_boundary_is_unchanged(TimeSpan offset)
    {
        var provided = new DateTimeOffset(2026, 5, 6, 7, 8, 9, offset);
        var credential = ScimServicePrincipalCredential.CreateWithToken(
            "scim-client",
            "raw-token",
            expiresAt: provided);

        // Exactly at the instant is treated as expired; one tick before is still valid.
        Assert.False(credential.CanAuthenticate(provided));
        Assert.True(credential.CanAuthenticate(provided.AddTicks(-1)));
        Assert.False(credential.CanAuthenticate(provided.AddTicks(1)));
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
