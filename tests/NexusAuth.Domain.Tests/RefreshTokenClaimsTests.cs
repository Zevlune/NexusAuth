using NexusAuth.Domain.Entities;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class RefreshTokenClaimsTests
{
    [Fact]
    public void Create_preserves_claims_json()
    {
        const string claimsJson = """{"userinfo":{"email":{"essential":true}}}""";

        var token = RefreshToken.Create(
            "web-client",
            Guid.NewGuid(),
            $"{OidcScopes.OpenId} {OidcScopes.Email}",
            claimsJson: claimsJson).Entity;

        Assert.Equal(claimsJson, token.ClaimsJson);
    }

    [Fact]
    public void Create_allows_claims_json_to_be_null()
    {
        var token = RefreshToken.Create(
            "web-client",
            Guid.NewGuid(),
            OidcScopes.OpenId).Entity;

        Assert.Null(token.ClaimsJson);
    }

    [Fact]
    public void Create_keeps_lifetime_position_and_accepts_claims_json_after_it()
    {
        const string claimsJson = """{"id_token":{"auth_time":{"essential":true}}}""";

        var token = RefreshToken.Create(
            "web-client",
            Guid.NewGuid(),
            OidcScopes.OpenId,
            TimeSpan.FromMinutes(5),
            claimsJson).Entity;

        Assert.Equal(claimsJson, token.ClaimsJson);
        Assert.Equal(TimeSpan.FromMinutes(5), token.ExpiresAt - token.CreatedAt);
    }
}
