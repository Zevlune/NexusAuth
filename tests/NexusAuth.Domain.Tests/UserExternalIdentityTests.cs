using NexusAuth.Domain.Entities;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class UserExternalIdentityTests
{
    [Fact]
    public void Create_normalizes_identity_values_and_initializes_utc_timestamps()
    {
        var timestamp = new DateTimeOffset(2026, 10, 20, 18, 30, 0, TimeSpan.FromHours(8));

        var identity = UserExternalIdentity.Create(
            Guid.NewGuid(),
            " oidc ",
            " https://issuer.example ",
            " subject-1 ",
            " union-1 ",
            timestamp);

        Assert.Equal("oidc", identity.Provider);
        Assert.Equal("https://issuer.example", identity.Issuer);
        Assert.Equal("subject-1", identity.Subject);
        Assert.Equal("union-1", identity.UnionId);
        Assert.Equal(TimeSpan.Zero, identity.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, identity.LastAuthenticatedAt.Offset);
        Assert.Equal(timestamp, identity.CreatedAt);
        Assert.Equal(timestamp, identity.LastAuthenticatedAt);
    }

    [Fact]
    public void UpdateLastAuthenticatedAt_advances_and_never_moves_backwards()
    {
        var createdAt = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var identity = UserExternalIdentity.Create(
            Guid.NewGuid(),
            "oidc",
            "https://issuer.example",
            "subject-1",
            now: createdAt);
        var latest = createdAt.AddMinutes(5);

        identity.UpdateLastAuthenticatedAt(latest);
        identity.UpdateLastAuthenticatedAt(createdAt.AddMinutes(1));

        Assert.Equal(latest, identity.LastAuthenticatedAt);
    }
}
