using System.IdentityModel.Tokens.Jwt;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class OidcIdTokenClaimsSectionSecurityTests
{
    private const string ProfileScope = $"{OidcScopes.OpenId} {OidcScopes.Profile}";

    [Theory]
    [InlineData(
        """{"id_token":{"email_verified":null}}""",
        OidcScopes.OpenId + " " + OidcScopes.Email,
        "email_verified",
        "email")]
    [InlineData(
        """{"id_token":{"phone_number_verified":null}}""",
        OidcScopes.OpenId + " " + OidcScopes.Phone,
        "phone_number_verified",
        "phone_number")]
    public async Task Verified_flag_request_does_not_emit_its_value_claim(
        string claimsJson,
        string scope,
        string verifiedClaim,
        string valueClaim)
    {
        var user = User.Create(
            "section-user",
            "Test-password-123!",
            "Section User",
            email: "section@example.com",
            phoneNumber: "13800000000");
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync(
            "client",
            scope,
            userId: user.Id,
            claimsJson: claimsJson);

        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(
            await harness.Service.IssueIdTokenAsync(
                "client",
                user.Id,
                null,
                accessToken,
                claimsJson));

        Assert.Contains(idToken.Claims, claim => claim.Type == verifiedClaim && claim.Value == "false");
        Assert.DoesNotContain(idToken.Claims, claim => claim.Type == valueClaim);
    }

    [Fact]
    public async Task Userinfo_constraints_do_not_override_id_token_profile_defaults()
    {
        const string claimsJson = """
            {
              "userinfo": {
                "name": { "value": "Different Name" },
                "preferred_username": { "value": "different-user" }
              }
            }
            """;
        var user = User.Create("profile-user", "Test-password-123!", "Profile User");
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync(
            "client",
            ProfileScope,
            userId: user.Id,
            claimsJson: claimsJson);

        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(
            await harness.Service.IssueIdTokenAsync(
                "client",
                user.Id,
                null,
                accessToken,
                claimsJson));

        Assert.Contains(idToken.Claims, claim => claim.Type == "name" && claim.Value == user.Nickname);
        Assert.Contains(idToken.Claims, claim => claim.Type == "preferred_username" && claim.Value == user.Username);
    }

    [Fact]
    public async Task Explicit_id_token_profile_constraints_are_enforced()
    {
        const string claimsJson = """
            {
              "id_token": {
                "name": { "value": "Different Name" },
                "preferred_username": { "values": ["different-user"] }
              }
            }
            """;
        var user = User.Create("profile-user", "Test-password-123!", "Profile User");
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var accessToken = await harness.Service.IssueAccessTokenAsync(
            "client",
            ProfileScope,
            userId: user.Id,
            claimsJson: claimsJson);

        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(
            await harness.Service.IssueIdTokenAsync(
                "client",
                user.Id,
                null,
                accessToken,
                claimsJson));

        Assert.DoesNotContain(idToken.Claims, claim => claim.Type is "name" or "preferred_username");
    }
}
