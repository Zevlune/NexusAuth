using FrameworkOptions = Microsoft.Extensions.Options.Options;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class UserTokenValidatorTests
{
    [Fact]
    public async Task Active_user_with_matching_epoch_is_active()
    {
        var harness = new ValidatorHarness();
        var user = CreateUser("alice");
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        harness.Add(user);
        var principal = Principal(
            Sub(user.Id),
            Jti("jti-1"),
            Epoch(user.TokenInvalidBefore!.Value.UtcTicks));

        Assert.True(await harness.Validator.IsActiveAsync(principal));
    }

    [Fact]
    public async Task Epoch_that_does_not_match_the_user_cutoff_is_rejected()
    {
        var harness = new ValidatorHarness();
        var user = CreateUser("alice");
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        harness.Add(user);
        var cutoffTicks = user.TokenInvalidBefore!.Value.UtcTicks;

        var staleEpoch = await harness.Validator.IsActiveAsync(
            Principal(Sub(user.Id), Jti("jti-stale"), Epoch(0)));
        var futureEpoch = await harness.Validator.IsActiveAsync(
            Principal(Sub(user.Id), Jti("jti-future"), Epoch(cutoffTicks + 10)));
        var unparsableEpoch = await harness.Validator.IsActiveAsync(
            Principal(Sub(user.Id), Jti("jti-bad"), new Claim(UserTokenValidator.EpochClaim, "not-a-number", ClaimValueTypes.Integer64)));

        Assert.False(staleEpoch);
        Assert.False(futureEpoch);
        Assert.False(unparsableEpoch);
    }

    [Fact]
    public async Task Missing_or_inactive_user_is_rejected()
    {
        var missingHarness = new ValidatorHarness();
        var unknownId = Guid.NewGuid();
        var missing = await missingHarness.Validator.IsActiveAsync(Principal(Sub(unknownId)));

        var inactiveHarness = new ValidatorHarness();
        var inactive = CreateUser("inactive", active: false);
        inactiveHarness.Add(inactive);
        var rejected = await inactiveHarness.Validator.IsActiveAsync(Principal(Sub(inactive.Id), Epoch(0)));

        Assert.False(missing);
        Assert.False(rejected);
    }

    [Fact]
    public async Task Legacy_token_without_epoch_is_accepted_before_any_revocation()
    {
        var harness = new ValidatorHarness();
        var user = CreateUser("alice");
        harness.Add(user);

        var withoutIat = await harness.Validator.IsActiveAsync(Principal(Sub(user.Id), Jti("jti-legacy")));
        var withIat = await harness.Validator.IsActiveAsync(
            Principal(Sub(user.Id), Jti("jti-legacy-iat"), Iat(DateTimeOffset.UtcNow.ToUnixTimeSeconds())));

        Assert.True(withoutIat);
        Assert.True(withIat);
    }

    [Fact]
    public async Task Legacy_token_without_epoch_requires_iat_strictly_after_the_cutoff()
    {
        var harness = new ValidatorHarness();
        var user = CreateUser("alice");
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        harness.Add(user);
        var cutoffSecond = user.TokenInvalidBefore!.Value.ToUnixTimeSeconds();

        var sameSecond = await harness.Validator.IsActiveAsync(
            Principal(Sub(user.Id), Jti("jti-same-second"), Iat(cutoffSecond)));
        var nextSecond = await harness.Validator.IsActiveAsync(
            Principal(Sub(user.Id), Jti("jti-next-second"), Iat(cutoffSecond + 1)));

        Assert.False(sameSecond);
        Assert.True(nextSecond);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("9999999999999")]
    public async Task Legacy_token_with_missing_malformed_or_out_of_range_iat_is_rejected_when_cutoff_set(string? iat)
    {
        var harness = new ValidatorHarness();
        var user = CreateUser("alice");
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        harness.Add(user);
        var claims = new List<Claim> { Sub(user.Id), Jti($"jti-{iat ?? "missing"}") };
        if (iat is not null)
            claims.Add(new Claim(JwtRegisteredClaimNames.Iat, iat, ClaimValueTypes.Integer64));

        Assert.False(await harness.Validator.IsActiveAsync(Principal(claims.ToArray())));
    }

    [Fact]
    public async Task Blacklisted_jti_is_rejected_even_with_valid_user_and_epoch()
    {
        var harness = new ValidatorHarness();
        var user = CreateUser("alice");
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        harness.Add(user);
        harness.BlacklistedJtis.Add("jti-revoked");
        var principal = Principal(
            Sub(user.Id),
            Jti("jti-revoked"),
            Epoch(user.TokenInvalidBefore!.Value.UtcTicks));

        Assert.False(await harness.Validator.IsActiveAsync(principal));
    }

    [Fact]
    public async Task Client_subject_bypasses_user_lookup_but_still_honors_blacklist()
    {
        var harness = new ValidatorHarness();
        var clientPrincipal = Principal(Sub("service-client"), Jti("client-jti"));

        Assert.True(await harness.Validator.IsActiveAsync(clientPrincipal));
        Assert.Empty(harness.UserLookups);

        harness.BlacklistedJtis.Add("client-jti");

        Assert.False(await harness.Validator.IsActiveAsync(clientPrincipal));
        Assert.Empty(harness.UserLookups);
    }

    [Fact]
    public async Task Empty_subject_is_rejected()
    {
        var harness = new ValidatorHarness();
        var user = CreateUser("alice");
        harness.Add(user);

        Assert.False(await harness.Validator.IsActiveAsync(Principal(Jti("jti-subject-less"), Epoch(0))));
    }

    [Fact]
    public async Task Long_claim_type_fallbacks_are_supported()
    {
        var harness = new ValidatorHarness();
        var user = CreateUser("alice");
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        harness.Add(user);
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.SerialNumber, "jti-long"),
            Epoch(user.TokenInvalidBefore!.Value.UtcTicks));

        Assert.True(await harness.Validator.IsActiveAsync(principal));
    }

    [Fact]
    public async Task Signed_tokens_from_two_clients_stay_active_until_a_same_second_cutoff()
    {
        using var harness = new TokenServiceHarness();
        var user = CreateUser("alice");
        harness.Add(user);

        var first = await harness.Service.IssueAccessTokenWithMetadataAsync(
            "client-a", "orders.read", TokenServiceHarness.Audience, user.Id);
        var second = await harness.Service.IssueAccessTokenWithMetadataAsync(
            "client-b", "orders.read", TokenServiceHarness.Audience, user.Id);

        var firstActive = await harness.Service.IntrospectAsync(first.AccessToken);
        var secondActive = await harness.Service.IntrospectAsync(second.AccessToken);

        Assert.True(firstActive.Active);
        Assert.Equal(user.Id.ToString(), firstActive.Subject);
        Assert.Equal("client-a", firstActive.ClientId);
        Assert.True(secondActive.Active);
        Assert.Equal("client-b", secondActive.ClientId);

        // The cutoff lands in the same wall-clock second as the signed token's iat.
        var iat = ReadIatSeconds(first.AccessToken);
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(iat).AddMilliseconds(500);
        Assert.Equal(iat, cutoff.ToUnixTimeSeconds());
        user.InvalidateTokens(cutoff);

        Assert.False((await harness.Service.IntrospectAsync(first.AccessToken)).Active);
        Assert.False((await harness.Service.IntrospectAsync(second.AccessToken)).Active);
        Assert.Empty(harness.ApiResourceCalls);
    }

    [Fact]
    public async Task Fresh_token_carries_exact_epoch_and_a_same_second_repeat_cutoff_invalidates_it()
    {
        using var harness = new TokenServiceHarness();
        var user = CreateUser("alice");
        harness.Add(user);

        var reference = await harness.Service.IssueAccessTokenWithMetadataAsync(
            "client-a", "orders.read", TokenServiceHarness.Audience, user.Id);
        var iat = ReadIatSeconds(reference.AccessToken);
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(iat).AddMilliseconds(500);
        Assert.Equal(iat, cutoff.ToUnixTimeSeconds());
        user.InvalidateTokens(cutoff);
        var firstEpoch = user.TokenInvalidBefore!.Value.UtcTicks;

        var fresh = await harness.Service.IssueAccessTokenWithMetadataAsync(
            "client-a", "orders.read", TokenServiceHarness.Audience, user.Id);
        Assert.Equal(firstEpoch.ToString(CultureInfo.InvariantCulture), ReadEpoch(fresh.AccessToken));
        Assert.True((await harness.Service.IntrospectAsync(fresh.AccessToken)).Active);
        Assert.False((await harness.Service.IntrospectAsync(reference.AccessToken)).Active);

        // Repeating the cutoff in the same second must advance the epoch by the microsecond stride.
        user.InvalidateTokens(cutoff);
        Assert.Equal(firstEpoch + 10, user.TokenInvalidBefore!.Value.UtcTicks);
        Assert.Equal(iat, user.TokenInvalidBefore!.Value.ToUnixTimeSeconds());
        Assert.False((await harness.Service.IntrospectAsync(fresh.AccessToken)).Active);

        var newest = await harness.Service.IssueAccessTokenWithMetadataAsync(
            "client-a", "orders.read", TokenServiceHarness.Audience, user.Id);
        Assert.Equal(
            user.TokenInvalidBefore!.Value.UtcTicks.ToString(CultureInfo.InvariantCulture),
            ReadEpoch(newest.AccessToken));
        Assert.True((await harness.Service.IntrospectAsync(newest.AccessToken)).Active);
    }

    [Fact]
    public async Task Disabled_then_reactivated_user_keeps_the_old_token_invalid()
    {
        using var harness = new TokenServiceHarness();
        var user = CreateUser("alice");
        harness.Add(user);

        var token = await harness.Service.IssueAccessTokenWithMetadataAsync(
            "client-a", "orders.read", TokenServiceHarness.Audience, user.Id);
        Assert.True((await harness.Service.IntrospectAsync(token.AccessToken)).Active);

        user.UpdateScimProfile(user.Username, user.Nickname, isActive: false);
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        Assert.False((await harness.Service.IntrospectAsync(token.AccessToken)).Active);

        user.UpdateScimProfile(user.Username, user.Nickname, isActive: true);
        Assert.True(user.IsActive);
        Assert.False((await harness.Service.IntrospectAsync(token.AccessToken)).Active);

        var reissued = await harness.Service.IssueAccessTokenWithMetadataAsync(
            "client-a", "orders.read", TokenServiceHarness.Audience, user.Id);
        Assert.True((await harness.Service.IntrospectAsync(reissued.AccessToken)).Active);
    }

    [Fact]
    public async Task Token_for_a_missing_user_is_inactive()
    {
        using var harness = new TokenServiceHarness();
        var user = CreateUser("alice");
        harness.Add(user);
        var token = await harness.Service.IssueAccessTokenWithMetadataAsync(
            "client-a", "orders.read", TokenServiceHarness.Audience, user.Id);
        Assert.True((await harness.Service.IntrospectAsync(token.AccessToken)).Active);

        harness.Remove(user.Id);

        Assert.False((await harness.Service.IntrospectAsync(token.AccessToken)).Active);
    }

    [Fact]
    public async Task Client_credentials_token_introspects_active_and_its_jti_blacklist_is_enforced()
    {
        using var harness = new TokenServiceHarness();
        var token = await harness.Service.IssueAccessTokenWithMetadataAsync(
            "service-client", "orders.read", TokenServiceHarness.Audience);

        var active = await harness.Service.IntrospectAsync(token.AccessToken);
        Assert.True(active.Active);
        Assert.Equal("service-client", active.Subject);
        Assert.Equal("service-client", active.ClientId);
        Assert.Empty(harness.UserLookups);

        await harness.Service.RevokeAccessTokenAsync(token.AccessToken, "service-client");

        Assert.True(harness.IsBlacklisted(token.Jti));
        Assert.False((await harness.Service.IntrospectAsync(token.AccessToken)).Active);
        Assert.Empty(harness.UserLookups);
        Assert.Empty(harness.ApiResourceCalls);
    }

    [Fact]
    public async Task Revoked_refresh_token_is_rejected_by_RefreshAsync()
    {
        using var harness = new TokenServiceHarness();
        var user = CreateUser("alice");
        harness.Add(user);

        var raw = await harness.Service.IssueRefreshTokenAsync("client-a", user.Id, "orders.read");
        Assert.False(harness.IsRefreshTokenRevoked(raw));

        await harness.Service.RevokeRefreshTokenAsync(raw, "client-a");
        Assert.True(harness.IsRefreshTokenRevoked(raw));

        var blocked = await harness.Service.RefreshAsync(raw, "client-a");
        Assert.False(blocked.IsSuccess);
    }

    [Fact]
    public async Task Rotated_refresh_token_can_be_revoked_and_then_blocks_RefreshAsync()
    {
        using var harness = new TokenServiceHarness();
        var user = CreateUser("alice");
        harness.Add(user);

        var raw = await harness.Service.IssueRefreshTokenAsync("client-a", user.Id, "orders.read");
        var refreshed = await harness.Service.RefreshAsync(raw, "client-a");
        Assert.True(refreshed.IsSuccess);
        Assert.NotNull(refreshed.RefreshToken);

        var rotated = refreshed.RefreshToken!;
        Assert.False(harness.IsRefreshTokenRevoked(rotated));

        await harness.Service.RevokeRefreshTokenAsync(rotated, "client-a");
        Assert.True(harness.IsRefreshTokenRevoked(rotated));
        Assert.False((await harness.Service.RefreshAsync(rotated, "client-a")).IsSuccess);
    }

    [Fact]
    public async Task Revoke_all_user_tokens_blocks_outstanding_refresh_tokens()
    {
        using var harness = new TokenServiceHarness();
        var user = CreateUser("alice");
        harness.Add(user);

        var raw = await harness.Service.IssueRefreshTokenAsync("client-a", user.Id, "orders.read");
        Assert.False(harness.IsRefreshTokenRevoked(raw));

        await harness.Service.RevokeAllUserTokensAsync(user.Id);

        Assert.True(harness.IsRefreshTokenRevoked(raw));
        Assert.False((await harness.Service.RefreshAsync(raw, "client-a")).IsSuccess);
        Assert.NotNull(user.TokenInvalidBefore);
    }

    private static User CreateUser(string username, bool active = true)
    {
        var user = User.Create(username, "test-password", username);
        if (!active)
            user.UpdateScimProfile(user.Username, user.Nickname, isActive: false);
        return user;
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static Claim Sub(Guid userId) => new(JwtRegisteredClaimNames.Sub, userId.ToString());

    private static Claim Sub(string subject) => new(JwtRegisteredClaimNames.Sub, subject);

    private static Claim Jti(string jti) => new(JwtRegisteredClaimNames.Jti, jti);

    private static Claim Epoch(long ticks) =>
        new(UserTokenValidator.EpochClaim, ticks.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64);

    private static Claim Iat(long seconds) =>
        new(JwtRegisteredClaimNames.Iat, seconds.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64);

    private static long ReadIatSeconds(string token) =>
        ReadJwt(token).Payload[JwtRegisteredClaimNames.Iat] switch
        {
            long value => value,
            int value => value,
            string value => long.Parse(value, CultureInfo.InvariantCulture),
            var value => throw new InvalidOperationException($"Unexpected iat value '{value}'."),
        };

    private static string ReadEpoch(string token) =>
        ReadJwt(token).Payload[UserTokenValidator.EpochClaim] switch
        {
            long value => value.ToString(CultureInfo.InvariantCulture),
            int value => value.ToString(CultureInfo.InvariantCulture),
            string value => value,
            var value => throw new InvalidOperationException($"Unexpected epoch value '{value}'."),
        };

    private static JwtSecurityToken ReadJwt(string token) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token);

    private sealed class ValidatorHarness
    {
        private readonly Dictionary<Guid, User> users = [];

        public ValidatorHarness()
        {
            var userRepository = RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
            {
                if (method.Name != nameof(IUserRepository.FindByIdAsync))
                    return null;
                var id = (Guid)args![0]!;
                UserLookups.Add(id);
                return users.GetValueOrDefault(id);
            });
            var blacklist = RecordingDispatchProxy.Create<ITokenBlacklistRepository>((method, args) =>
                method.Name == nameof(ITokenBlacklistRepository.ExistsActiveAsync)
                    && BlacklistedJtis.Contains((string)args![0]!));
            Validator = new UserTokenValidator(userRepository, blacklist);
        }

        public HashSet<string> BlacklistedJtis { get; } = [];

        public List<Guid> UserLookups { get; } = [];

        public UserTokenValidator Validator { get; }

        public void Add(User user) => users[user.Id] = user;
    }

    private sealed class TokenServiceHarness : IDisposable
    {
        public const string Issuer = "https://issuer.example.test";
        public const string Audience = "orders.api";

        private readonly RSA rsa = RSA.Create(2048);
        private readonly Dictionary<Guid, User> users = [];
        private readonly Dictionary<string, RefreshToken> refreshTokensByHash = new(StringComparer.Ordinal);
        private readonly HashSet<string> blacklistedJtis = new(StringComparer.Ordinal);

        public TokenServiceHarness()
        {
            var signingKey = new RsaSecurityKey(rsa) { KeyId = "user-token-validator-test" };
            var userRepository = RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
            {
                if (method.Name != nameof(IUserRepository.FindByIdAsync))
                    return null;
                var id = (Guid)args![0]!;
                UserLookups.Add(id);
                return users.GetValueOrDefault(id);
            });
            var blacklist = RecordingDispatchProxy.Create<ITokenBlacklistRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(ITokenBlacklistRepository.ExistsActiveAsync):
                        return blacklistedJtis.Contains((string)args![0]!);
                    case nameof(ITokenBlacklistRepository.FindByJtiAsync):
                        return null;
                    case nameof(ITokenBlacklistRepository.AddAsync):
                        blacklistedJtis.Add(((TokenBlacklistEntry)args![0]!).Jti);
                        return null;
                    default:
                        return null;
                }
            });
            var refreshTokens = RecordingDispatchProxy.Create<IRefreshTokenRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IRefreshTokenRepository.AddAsync):
                        var added = (RefreshToken)args![0]!;
                        refreshTokensByHash[added.TokenHash] = added;
                        return null;
                    case nameof(IRefreshTokenRepository.FindByTokenAsync):
                        return refreshTokensByHash.GetValueOrDefault(RefreshToken.Hash((string)args![0]!));
                    case nameof(IRefreshTokenRepository.RevokeAsync):
                        refreshTokensByHash.Values.FirstOrDefault(token => token.Id == (Guid)args![0]!)?.Revoke();
                        return null;
                    case nameof(IRefreshTokenRepository.RevokeAllForUserAsync):
                        foreach (var token in refreshTokensByHash.Values.Where(token => token.UserId == (Guid)args![0]!))
                            token.Revoke();
                        return null;
                    case nameof(IRefreshTokenRepository.RotateAsync):
                        if (!refreshTokensByHash.TryGetValue((string)args![0]!, out var rotating) || rotating.IsRevoked)
                            return null;
                        rotating.Revoke();
                        var replacement = (RefreshToken)args![2]!;
                        refreshTokensByHash[replacement.TokenHash] = replacement;
                        return replacement;
                    default:
                        return null;
                }
            });
            var apiResources = RecordingDispatchProxy.Create<IApiResourceRepository>((method, args) =>
            {
                ApiResourceCalls.Add(method.Name);
                return method.Name == nameof(IApiResourceRepository.FindByAudiencesAsync)
                    ? Array.Empty<ApiResource>()
                    : null;
            });
            var clientService = RecordingDispatchProxy.Create<IClientService>((method, args) =>
                method.Name == nameof(IClientService.ValidateScopesAsync)
                    ? ScopeValidationResult.Success((string)args![1]!)
                    : null);
            Service = new TokenService(
                refreshTokens,
                blacklist,
                userRepository,
                apiResources,
                clientService,
                FrameworkOptions.Create(new JwtOptions
                {
                    Issuer = Issuer,
                    DefaultAudience = "unused.default",
                    AccessTokenLifetimeMinutes = 60,
                    RefreshTokenLifetimeMinutes = 60,
                }),
                new TestSigningCredentialsProvider(signingKey),
                NullLogger<TokenService>.Instance);
        }

        public List<string> ApiResourceCalls { get; } = [];

        public List<Guid> UserLookups { get; } = [];

        public TokenService Service { get; }

        public void Add(User user) => users[user.Id] = user;

        public void Remove(Guid id) => users.Remove(id);

        public bool IsBlacklisted(string jti) => blacklistedJtis.Contains(jti);

        public bool IsRefreshTokenRevoked(string rawToken) =>
            refreshTokensByHash.TryGetValue(RefreshToken.Hash(rawToken), out var token) && token.IsRevoked;

        public void Dispose() => rsa.Dispose();
    }

    private sealed class TestSigningCredentialsProvider(SecurityKey signingKey) : ITokenSigningCredentialsProvider
    {
        public string Algorithm => SecurityAlgorithms.RsaSha256;

        public string KeyId => signingKey.KeyId!;

        public SigningCredentials GetSigningCredentials() =>
            new(signingKey, SecurityAlgorithms.RsaSha256);

        public TokenValidationParameters CreateTokenValidationParameters(
            string issuer,
            string? audience = null,
            bool validateLifetime = true)
        {
            return new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = [signingKey],
                ValidateAudience = !string.IsNullOrWhiteSpace(audience),
                ValidAudience = audience,
                ValidateLifetime = validateLifetime,
                ClockSkew = TimeSpan.Zero,
            };
        }

        public object GetJwk() => throw new NotSupportedException();
    }
}
