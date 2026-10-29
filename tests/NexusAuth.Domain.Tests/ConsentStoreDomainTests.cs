using NexusAuth.Domain.Entities;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class ConsentStoreDomainTests
{
    [Fact]
    public void Request_uses_a_256_bit_identifier_and_expires_after_ten_minutes()
    {
        var userId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));

        var result = ConsentRequest.Create(userId, "client_id=client&scope=openid", createdAt);

        Assert.Equal(43, result.RawRequestId.Length);
        Assert.Equal(ConsentRequest.HashRequestId(result.RawRequestId), result.Entity.RequestIdHash);
        Assert.Equal(userId, result.Entity.UserId);
        Assert.Equal("client_id=client&scope=openid", result.Entity.Query);
        Assert.Equal(ConsentRequestStatus.Pending, result.Entity.Status);
        Assert.False(result.Entity.Remember);
        Assert.Equal(TimeSpan.Zero, result.Entity.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, result.Entity.ExpiresAt.Offset);
        Assert.Equal(TimeSpan.FromMinutes(10), result.Entity.ExpiresAt - result.Entity.CreatedAt);
    }

    [Fact]
    public void Scope_normalization_is_order_independent_and_case_sensitive()
    {
        Assert.Equal(
            "openid profile read",
            ConsentGrant.NormalizeScope(" profile  read openid profile "));
        Assert.NotEqual(
            ConsentGrant.NormalizeScope("openid profile"),
            ConsentGrant.NormalizeScope("OpenID profile"));
    }

    [Fact]
    public void Claims_match_key_preserves_exact_external_representation()
    {
        const string claims = """{"id_token":{"email":{"essential":true}}}""";

        Assert.Equal("n", ConsentGrant.CreateClaimsMatchKey(null));
        Assert.Equal(ConsentGrant.CreateClaimsMatchKey(claims), ConsentGrant.CreateClaimsMatchKey(claims));
        Assert.NotEqual(ConsentGrant.CreateClaimsMatchKey(claims), ConsentGrant.CreateClaimsMatchKey(claims + " "));
        Assert.Equal("n", ConsentGrant.CreateClaimsMatchKey(null));
    }
}
