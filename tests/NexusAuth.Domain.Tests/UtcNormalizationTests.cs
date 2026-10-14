using NexusAuth.Domain.Entities;
using Xunit;

namespace NexusAuth.Domain.Tests;

/// <summary>
/// Guards that every persisted <see cref="DateTimeOffset"/> produced by these
/// domain entities is normalized to UTC without changing the represented instant.
/// </summary>
public sealed class UtcNormalizationTests
{
    private static readonly TimeSpan PositiveOffset = TimeSpan.FromHours(8);
    private static readonly TimeSpan NegativeOffset = TimeSpan.FromHours(-5);

    private static void AssertNormalizedToUtc(DateTimeOffset original, DateTimeOffset persisted)
    {
        Assert.Equal(TimeSpan.Zero, persisted.Offset);
        Assert.Equal(original.UtcTicks, persisted.UtcTicks);
        Assert.Equal(original.UtcDateTime, persisted.UtcDateTime);
    }

    private static void AssertRecentUtc(DateTimeOffset value, DateTimeOffset before)
    {
        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.True(value >= before.AddSeconds(-5) && value <= DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void OpenApiCredential_normalizes_expires_at_for_positive_and_negative_offsets()
    {
        var positive = new DateTimeOffset(2030, 1, 2, 3, 4, 5, PositiveOffset);
        var negative = new DateTimeOffset(2030, 1, 2, 3, 4, 5, NegativeOffset);

        var fromPositive = OpenApiCredential.CreateWithToken(
            "positive", "token", OpenApiCredential.TargetTypeApplication, positive);
        var fromNegative = OpenApiCredential.CreateWithToken(
            "negative", "token", OpenApiCredential.TargetTypeApplication, negative);

        AssertNormalizedToUtc(positive, fromPositive.ExpiresAt!.Value);
        AssertNormalizedToUtc(negative, fromNegative.ExpiresAt!.Value);
    }

    [Fact]
    public void OpenApiCredential_keeps_null_expires_at_and_defaults_created_at_to_utc_now()
    {
        var before = DateTimeOffset.UtcNow;
        var credential = OpenApiCredential.CreateWithToken(
            "no-expiry", "token", OpenApiCredential.TargetTypeApplication, expiresAt: null);

        Assert.Null(credential.ExpiresAt);
        AssertRecentUtc(credential.CreatedAt, before);
    }

    [Fact]
    public void OpenApiCredential_normalizes_expires_at_on_update_and_last_used_at()
    {
        var credential = OpenApiCredential.CreateWithToken(
            "reader", "token", OpenApiCredential.TargetTypeApplication);
        var positive = new DateTimeOffset(2031, 5, 6, 7, 8, 9, PositiveOffset);
        var negative = new DateTimeOffset(2031, 5, 6, 7, 8, 9, NegativeOffset);

        credential.Update("reader", positive, isActive: true);
        AssertNormalizedToUtc(positive, credential.ExpiresAt!.Value);

        credential.RecordUse(negative);
        AssertNormalizedToUtc(negative, credential.LastUsedAt!.Value);

        var before = DateTimeOffset.UtcNow;
        credential.RecordUse();
        AssertRecentUtc(credential.LastUsedAt!.Value, before);
    }

    [Fact]
    public void UserCredential_normalizes_expires_at_and_timestamps()
    {
        var positiveNow = new DateTimeOffset(2032, 2, 3, 4, 5, 6, PositiveOffset);
        var expiresAt = new DateTimeOffset(2032, 2, 3, 5, 5, 6, NegativeOffset);

        var credential = UserCredential.CreatePendingTotp(
            Guid.NewGuid(), "protected-secret", expiresAt, now: positiveNow);

        AssertNormalizedToUtc(expiresAt, credential.PendingExpiresAt!.Value);
        AssertNormalizedToUtc(positiveNow, credential.CreatedAt);
        AssertNormalizedToUtc(positiveNow, credential.UpdatedAt);
    }

    [Fact]
    public void UserCredential_normalizes_updated_and_disabled_timestamps()
    {
        var createdNow = new DateTimeOffset(2033, 6, 7, 8, 0, 0, PositiveOffset);
        var expiresAt = new DateTimeOffset(2033, 6, 7, 9, 0, 0, PositiveOffset);
        var credential = UserCredential.CreatePendingTotp(
            Guid.NewGuid(), "protected-secret", expiresAt, now: createdNow);
        var confirmNow = new DateTimeOffset(2033, 6, 7, 8, 9, 10, PositiveOffset);

        Assert.True(credential.ConfirmTotp("protected-secret", 1, confirmNow));
        AssertNormalizedToUtc(confirmNow, credential.UpdatedAt);

        var useNow = new DateTimeOffset(2033, 6, 7, 8, 10, 10, NegativeOffset);
        Assert.True(credential.TryUseTotpCounter(2, useNow));
        AssertNormalizedToUtc(useNow, credential.UpdatedAt);

        var disableNow = new DateTimeOffset(2033, 6, 7, 8, 11, 10, PositiveOffset);
        credential.Disable(disableNow);
        AssertNormalizedToUtc(disableNow, credential.DisabledAt!.Value);
        AssertNormalizedToUtc(disableNow, credential.UpdatedAt);
    }

    [Fact]
    public void UserCredential_default_now_is_utc()
    {
        var before = DateTimeOffset.UtcNow;
        var credential = UserCredential.CreatePendingTotp(
            Guid.NewGuid(), "protected-secret", DateTimeOffset.UtcNow.AddMinutes(5));

        AssertRecentUtc(credential.CreatedAt, before);
        AssertRecentUtc(credential.UpdatedAt, before);
    }

    [Fact]
    public void WebAuthnCredential_normalizes_created_last_used_and_disabled_timestamps()
    {
        var createdNow = new DateTimeOffset(2034, 3, 4, 5, 6, 7, PositiveOffset);
        var credential = WebAuthnCredential.Create(
            Guid.NewGuid(),
            [1, 2, 3],
            [4, 5, 6],
            signatureCounter: 10,
            aaguid: Guid.NewGuid(),
            transports: null,
            isBackupEligible: false,
            isBackedUp: false,
            displayName: null,
            now: createdNow);

        AssertNormalizedToUtc(createdNow, credential.CreatedAt);

        var authNow = new DateTimeOffset(2034, 3, 4, 5, 7, 7, NegativeOffset);
        credential.RecordAuthentication(11, false, authNow);
        AssertNormalizedToUtc(authNow, credential.LastUsedAt!.Value);
        Assert.Equal(11u, credential.SignatureCounter);

        var disableNow = new DateTimeOffset(2034, 3, 4, 5, 8, 7, PositiveOffset);
        credential.Disable(disableNow);
        AssertNormalizedToUtc(disableNow, credential.DisabledAt!.Value);
    }

    [Fact]
    public void WebAuthnCredential_default_now_is_utc()
    {
        var before = DateTimeOffset.UtcNow;
        var credential = WebAuthnCredential.Create(
            Guid.NewGuid(),
            [1, 2, 3],
            [4, 5, 6],
            signatureCounter: 0,
            aaguid: Guid.NewGuid(),
            transports: null,
            isBackupEligible: false,
            isBackedUp: false);

        AssertRecentUtc(credential.CreatedAt, before);
    }

    [Fact]
    public void WebAuthnChallenge_normalizes_expires_and_consumed_timestamps()
    {
        var expiresAt = new DateTimeOffset(2035, 4, 5, 6, 7, 8, PositiveOffset);
        var createdNow = new DateTimeOffset(2035, 4, 4, 6, 6, 8, NegativeOffset);

        var challenge = WebAuthnChallenge.Create(
            "flow-token",
            WebAuthnChallenge.AuthenticationPurpose,
            userId: null,
            optionsJson: "{}",
            returnUrl: null,
            rememberMe: false,
            expiresAt: expiresAt,
            now: createdNow);

        AssertNormalizedToUtc(expiresAt, challenge.ExpiresAt);
        AssertNormalizedToUtc(createdNow, challenge.CreatedAt);

        var consumeNow = new DateTimeOffset(2035, 4, 5, 6, 6, 30, PositiveOffset);
        Assert.True(challenge.TryConsume(consumeNow));
        AssertNormalizedToUtc(consumeNow, challenge.ConsumedAt!.Value);
    }

    [Fact]
    public void WebAuthnChallenge_default_now_is_utc_and_expiry_boundary_is_preserved()
    {
        var before = DateTimeOffset.UtcNow;
        var challenge = WebAuthnChallenge.Create(
            "flow-token",
            WebAuthnChallenge.RegistrationPurpose,
            Guid.NewGuid(),
            optionsJson: "{}",
            returnUrl: null,
            rememberMe: false,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

        AssertRecentUtc(challenge.CreatedAt, before);

        // Consuming at exactly the expiry instant must still fail, regardless of offset.
        var boundary = challenge.ExpiresAt.ToOffset(PositiveOffset);
        Assert.Equal(TimeSpan.Zero, challenge.ExpiresAt.Offset);
        Assert.False(challenge.TryConsume(boundary));
        Assert.Null(challenge.ConsumedAt);
    }

    [Fact]
    public void Create_rejects_non_positive_lifetime_for_any_offset()
    {
        var now = new DateTimeOffset(2036, 7, 8, 9, 10, 11, NegativeOffset);

        Assert.Throws<ArgumentOutOfRangeException>(() => WebAuthnChallenge.Create(
            "flow-token",
            WebAuthnChallenge.AuthenticationPurpose,
            userId: null,
            optionsJson: "{}",
            returnUrl: null,
            rememberMe: false,
            expiresAt: new DateTimeOffset(2036, 7, 8, 9, 10, 11, PositiveOffset),
            now: now));
    }
}
