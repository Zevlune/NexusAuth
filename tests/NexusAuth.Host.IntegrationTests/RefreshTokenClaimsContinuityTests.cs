using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.Authorization;
using NexusAuth.Application.Services.OIDC;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Host.TokenEndpoint;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class RefreshTokenClaimsContinuityTests
{
    [Fact]
    public async Task Refresh_preserves_claims_and_original_grant_scope_across_two_rotations()
    {
        const string clientId = "web-app";
        const string originalGrantScope =
            $"{OidcScopes.OpenId} {OidcScopes.Profile} {OidcScopes.Email} {OidcScopes.OfflineAccess}";
        const string narrowedAccessScope =
            $"{OidcScopes.OpenId} {OidcScopes.Email} {OidcScopes.OfflineAccess}";
        const string claimsJson = """
            {
              "userinfo": {
                "email": { "value": "alice@example.com" }
              }
            }
            """;

        var user = User.Create(
            "claims-user",
            "Test-password-123!",
            "Claims User",
            email: "alice@example.com");
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);

        var client = OAuthClient.Create(
            clientId,
            "Claims client",
            allowedScopes: [OidcScopes.OpenId, OidcScopes.Profile, OidcScopes.Email, OidcScopes.OfflineAccess],
            allowedGrantTypes: [OAuthGrantTypes.AuthorizationCode],
            tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.ClientSecretPost);
        var strategy = new AuthorizationCodeGrantStrategy(
            HardeningProxy.Create<IAuthorizationService>((method, _) =>
                method.Name == nameof(IAuthorizationService.ValidateAndConsumeCodeAsync)
                    ? AuthorizationCodeResult.Success(
                        user.Id,
                        clientId,
                        originalGrantScope,
                        "nonce",
                        claimsJson,
                        DateTimeOffset.UtcNow.AddMinutes(-1),
                        "acr",
                        "pwd")
                    : null),
            harness.Service,
            HardeningClientServices.AcceptingAuthorizeRequests(client: client),
            HardeningProxy.Create<IUserService>((method, _) =>
                method.Name == nameof(IUserService.FindByIdAsync) ? user : null),
            harness.Policy.Service,
            Options.Create(new JwtOptions
            {
                Issuer = InMemoryTokenServiceHarness.Issuer,
                AccessTokenLifetimeMinutes = 60,
            }));
        var context = new TokenGrantContext(
            new DefaultHttpContext(),
            new TokenRequest(
                OAuthGrantTypes.AuthorizationCode,
                clientId,
                "secret",
                Code: "code",
                RedirectUri: "https://client/callback",
                CodeVerifier: "verifier"));
        var authorizationResult = Assert.IsType<OkObjectResult>(
            await strategy.ExecuteAsync(context, CancellationToken.None));
        var authorizationResponse = JsonSerializer.SerializeToElement(authorizationResult.Value);
        var original = authorizationResponse.GetProperty("refresh_token").GetString()!;
        Assert.Equal(claimsJson, harness.GetRefreshToken(original).ClaimsJson);

        var firstRefresh = await harness.Service.RefreshAsync(
            original,
            clientId,
            scope: narrowedAccessScope);
        Assert.True(firstRefresh.IsSuccess);
        Assert.Equal(narrowedAccessScope, firstRefresh.Scope);
        Assert.Equal(claimsJson, ReadClaimsJson(firstRefresh.AccessToken!));

        var firstRotated = harness.GetRefreshToken(firstRefresh.RefreshToken!);
        Assert.Equal(originalGrantScope, firstRotated.Scope);
        Assert.Equal(claimsJson, firstRotated.ClaimsJson);

        var secondRefresh = await harness.Service.RefreshAsync(firstRefresh.RefreshToken!, clientId);
        Assert.True(secondRefresh.IsSuccess);
        Assert.Equal(originalGrantScope, secondRefresh.Scope);
        Assert.Equal(claimsJson, ReadClaimsJson(secondRefresh.AccessToken!));

        var secondRotated = harness.GetRefreshToken(secondRefresh.RefreshToken!);
        Assert.Equal(originalGrantScope, secondRotated.Scope);
        Assert.Equal(claimsJson, secondRotated.ClaimsJson);

        var requestedClaims = OidcRequestedClaims.Parse(claimsJson);
        Assert.True(OidcClaimEmissionPolicy.ShouldEmitUserInfoClaim(
            requestedClaims,
            "email",
            "alice@example.com"));
        Assert.False(OidcClaimEmissionPolicy.ShouldEmitUserInfoClaim(
            requestedClaims,
            "email",
            "different@example.com"));
    }

    private static string ReadClaimsJson(string accessToken)
    {
        return new JwtSecurityTokenHandler()
            .ReadJwtToken(accessToken)
            .Claims
            .Single(claim => claim.Type == "claims_json")
            .Value;
    }
}
