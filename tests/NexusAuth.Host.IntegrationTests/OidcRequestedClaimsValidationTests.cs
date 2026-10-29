using NexusAuth.Application.Services.OIDC;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class OidcRequestedClaimsValidationTests
{
    public static TheoryData<string> InvalidClaims => new()
    {
        """{"userinfo":null}""",
        """{"id_token":[]}""",
        """{"userinfo":{},"userinfo":{}}""",
        """{"userinfo":{"email":null,"email":null}}""",
        """{"id_token":{"email":{},"email":{}}}""",
        """{"userinfo":{"email":{"essential":"true"}}}""",
        """{"userinfo":{"email":{"value":[]}}}""",
        """{"userinfo":{"email":{"value":{}}}}""",
        """{"userinfo":{"email":{"values":{}}}}""",
        """{"userinfo":{"email":{"value":"alice@example.com","values":["alice@example.com"]}}}""",
        """{"userinfo":{"email":{"values":[]}}}""",
        """{"userinfo":{"email":{"values":["alice@example.com",42]}}}""",
        """{"userinfo":{"email_verified":{"values":[true,"false"]}}}""",
        """{"userinfo":{"email_verified":{"values":[true,1]}}}""",
        """{"userinfo":{"email_verified":{"values":[true,null]}}}""",
        """{"userinfo":{"email_verified":{"values":[true,{}]}}}""",
        """{"userinfo":{"email":{"values":["alice@example.com",""]}}}""",
    };

    [Theory]
    [MemberData(nameof(InvalidClaims))]
    public void Parse_rejects_malformed_claim_structure(string claimsJson)
    {
        Assert.Throws<InvalidOperationException>(() => OidcRequestedClaims.Parse(claimsJson));
    }

    [Fact]
    public void Parse_allows_unknown_extensions_and_preserves_section_specific_requests()
    {
        const string claimsJson = """
            {
              "extension": {
                "ignored": true
              },
              "userinfo": {
                "email": {
                  "essential": true,
                  "values": ["userinfo@example.com"],
                  "vendor-extension": {
                    "ignored": true
                  }
                }
              },
              "id_token": {
                "email": {
                  "value": "id-token@example.com"
                }
              }
            }
            """;

        var requestedClaims = OidcRequestedClaims.Parse(claimsJson);
        var userInfoRequest = requestedClaims.UserInfoClaimRequests["email"];
        var idTokenRequest = requestedClaims.IdTokenClaimRequests["email"];

        Assert.True(userInfoRequest.Essential);
        Assert.Equal(["userinfo@example.com"], userInfoRequest.Values);
        Assert.Equal("id-token@example.com", idTokenRequest.Value);
        Assert.True(OidcClaimEmissionPolicy.ShouldEmitUserInfoClaim(
            requestedClaims,
            "email",
            "userinfo@example.com"));
        Assert.False(OidcClaimEmissionPolicy.ShouldEmitUserInfoClaim(
            requestedClaims,
            "email",
            "id-token@example.com"));
        Assert.True(OidcClaimEmissionPolicy.ShouldEmitIdTokenClaim(
            requestedClaims,
            "email",
            "id-token@example.com"));
        Assert.False(OidcClaimEmissionPolicy.ShouldEmitIdTokenClaim(
            requestedClaims,
            "email",
            "userinfo@example.com"));

        var created = OidcRequestedClaims.CreateRequests(
            requestedClaims.IdTokenClaimRequests,
            requestedClaims.UserInfoClaimRequests);

        Assert.Same(userInfoRequest, created.UserInfoClaimRequests["email"]);
        Assert.Same(idTokenRequest, created.IdTokenClaimRequests["email"]);
    }

    [Fact]
    public void Parse_wraps_invalid_json_as_json_exception_for_the_authorization_layer()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => OidcRequestedClaims.Parse("""{"userinfo":"""));
    }

    [Fact]
    public void Parse_preserves_boolean_and_number_scalar_constraints()
    {
        const string claimsJson = """
            {
              "userinfo": {
                "email_verified": {
                  "value": false
                }
              },
              "id_token": {
                "auth_time": {
                  "values": [123, 1.25, 1e3]
                }
              }
            }
            """;

        var requestedClaims = OidcRequestedClaims.Parse(claimsJson);

        Assert.Equal("false", requestedClaims.UserInfoClaimRequests["email_verified"].Value);
        Assert.Equal(
            OidcClaimScalarKind.Boolean,
            requestedClaims.UserInfoClaimRequests["email_verified"].ValueKind);
        Assert.Equal(
            ["123", "1.25", "1e3"],
            requestedClaims.IdTokenClaimRequests["auth_time"].Values);
        Assert.Equal(
            OidcClaimScalarKind.Number,
            requestedClaims.IdTokenClaimRequests["auth_time"].ValuesKind);
        Assert.True(requestedClaims.UserInfoClaimRequests["email_verified"].Matches(
            "false",
            OidcClaimScalarKind.Boolean));
        Assert.False(requestedClaims.UserInfoClaimRequests["email_verified"].Matches(
            "false",
            OidcClaimScalarKind.String));
        var stringFalseClaims = OidcRequestedClaims.Parse(
            """{"userinfo":{"email_verified":{"value":"false"}}}""");
        Assert.False(OidcClaimEmissionPolicy.ShouldEmitUserInfoClaim(
            stringFalseClaims,
            "email_verified",
            "false"));
        Assert.True(OidcClaimEmissionPolicy.ShouldEmitUserInfoClaim(
            requestedClaims,
            "email_verified",
            "false"));
        Assert.False(OidcClaimEmissionPolicy.ShouldEmitUserInfoClaim(
            requestedClaims,
            "email_verified",
            "true"));
        Assert.True(OidcClaimEmissionPolicy.ShouldEmitIdTokenClaim(
            requestedClaims,
            "auth_time",
            "1.25"));
        Assert.True(OidcClaimEmissionPolicy.ShouldEmitIdTokenClaim(
            requestedClaims,
            "auth_time",
            "1000"));
        Assert.False(OidcClaimEmissionPolicy.ShouldEmitIdTokenClaim(
            requestedClaims,
            "auth_time",
            "2"));
    }

    [Fact]
    public void Matches_honors_scalar_kinds_and_manual_requests_default_to_string()
    {
        var requestedClaims = OidcRequestedClaims.Parse("""
            {
              "userinfo": {
                "email": {
                  "value": "42"
                }
              },
              "id_token": {
                "auth_time": {
                  "values": [1e3]
                }
              }
            }
            """);
        var emailRequest = requestedClaims.UserInfoClaimRequests["email"];
        var authTimeRequest = requestedClaims.IdTokenClaimRequests["auth_time"];

        Assert.False(emailRequest.Matches("42", OidcClaimScalarKind.Number));
        Assert.True(OidcClaimEmissionPolicy.ShouldEmitUserInfoClaim(
            requestedClaims,
            "email",
            "42"));
        var numericEmailClaims = OidcRequestedClaims.Parse(
            """{"userinfo":{"email":{"value":42}}}""");
        Assert.False(OidcClaimEmissionPolicy.ShouldEmitUserInfoClaim(
            numericEmailClaims,
            "email",
            "42"));
        Assert.True(authTimeRequest.Matches("1000", OidcClaimScalarKind.Number));
        Assert.False(authTimeRequest.Matches("1000", OidcClaimScalarKind.String));

        var manualRequest = new OidcClaimRequest(null, "value", []);

        Assert.True(manualRequest.Matches("value"));
        Assert.False(manualRequest.Matches("value", OidcClaimScalarKind.Number));
    }

    [Theory]
    [InlineData("1e3", "1000", true)]
    [InlineData("1e-100", "0", false)]
    [InlineData("1.00000000000000000000000000001", "1", false)]
    [InlineData("1e1000000000000", "10e999999999999", true)]
    [InlineData("-0.000e1234567890123", "0", true)]
    [InlineData("1e3", "+1000.0", true)]
    public void Matches_compares_json_numbers_by_exact_normalized_components(
        string requestedValue,
        string actualValue,
        bool expected)
    {
        var requestedClaims = OidcRequestedClaims.Parse(
            "{\"id_token\":{\"auth_time\":{\"value\":" + requestedValue + "}}}");

        Assert.Equal(expected, OidcClaimEmissionPolicy.ShouldEmitIdTokenClaim(
            requestedClaims,
            "auth_time",
            actualValue));
    }
}
