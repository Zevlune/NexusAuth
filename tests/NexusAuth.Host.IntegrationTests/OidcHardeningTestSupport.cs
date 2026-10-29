using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Shared.OAuth;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// Interface proxy used by the OIDC hardening tests. Unhandled Task methods complete successfully and
/// unhandled Task&lt;T&gt; methods return default(T), so a test only implements the members it exercises.
/// </summary>
public class HardeningProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> handler = null!;

    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, HardeningProxy>();
        ((HardeningProxy)(object)proxy).handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var result = handler(targetMethod, args);
        if (result is not null && targetMethod.ReturnType.IsInstanceOfType(result))
            return result;
        if (targetMethod.ReturnType == typeof(Task))
            return Task.CompletedTask;
        if (targetMethod.ReturnType.IsGenericType
            && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
            var value = result ?? (resultType.IsValueType ? Activator.CreateInstance(resultType) : null);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [value]);
        }

        return result;
    }
}

/// <summary>
/// Configurable user security policy. It deliberately does not implement <see cref="ISecurityPolicyService"/>
/// itself: the Luck module scans loaded assemblies for IScopedDependency implementations, so a concrete
/// test class would be auto-registered into every test host. The runtime proxy is not scanned.
/// </summary>
public sealed class TestSecurityPolicy
{
    public TestSecurityPolicy(Func<User, bool>? isAllowed = null)
    {
        Service = HardeningProxy.Create<ISecurityPolicyService>((method, args) =>
        {
            if (method.Name == nameof(ISecurityPolicyService.CheckClient))
                return PolicyCheckResult.Success();

            UserChecks++;
            var user = (User)args![0]!;
            var allowed = !BlockedUsers.Contains(user.Id) && (isAllowed?.Invoke(user) ?? true);
            return allowed ? PolicyCheckResult.Success() : PolicyCheckResult.Failure("User is blocked by security policy.");
        });
    }

    public HashSet<Guid> BlockedUsers { get; } = [];

    public int UserChecks { get; private set; }

    public ISecurityPolicyService Service { get; }

    public static TestSecurityPolicy Whitelist(params string[] allowedUsernames) =>
        new(user => allowedUsernames.Contains(user.Username, StringComparer.OrdinalIgnoreCase));
}

public sealed class HardeningSigningCredentialsProvider(SecurityKey signingKey) : ITokenSigningCredentialsProvider
{
    public string Algorithm => SecurityAlgorithms.RsaSha256;

    public string KeyId => signingKey.KeyId!;

    public SigningCredentials GetSigningCredentials() => new(signingKey, SecurityAlgorithms.RsaSha256);

    public TokenValidationParameters CreateTokenValidationParameters(string issuer, string? audience = null, bool validateLifetime = true)
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

    public object GetJwk() => new { kid = KeyId };
}

/// <summary>
/// A real <see cref="TokenService"/> backed by in-memory repositories.
/// </summary>
public sealed class InMemoryTokenServiceHarness : IDisposable
{
    public const string Issuer = "https://issuer.example";

    private readonly RSA rsa = RSA.Create(2048);
    private readonly Dictionary<Guid, User> users = [];
    private readonly Dictionary<string, RefreshToken> refreshTokensByHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenBlacklistEntry> blacklist = new(StringComparer.Ordinal);
    private readonly List<ApiResource> resources = [];
    private readonly HashSet<string> unavailableClientScopes = new(StringComparer.Ordinal);
    private bool clientIsActive = true;

    public InMemoryTokenServiceHarness(TestSecurityPolicy? policy = null)
    {
        Policy = policy ?? new TestSecurityPolicy();
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "oidc-hardening-test" };
        var userRepository = HardeningProxy.Create<IUserRepository>((method, args) =>
            method.Name == nameof(IUserRepository.FindByIdAsync) ? users.GetValueOrDefault((Guid)args![0]!) : null);
        var blacklistRepository = HardeningProxy.Create<ITokenBlacklistRepository>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(ITokenBlacklistRepository.AddAsync):
                    var entry = (TokenBlacklistEntry)args![0]!;
                    blacklist[entry.Jti] = entry;
                    return null;
                case nameof(ITokenBlacklistRepository.FindByJtiAsync):
                    return blacklist.GetValueOrDefault((string)args![0]!);
                case nameof(ITokenBlacklistRepository.ExistsActiveAsync):
                    return blacklist.TryGetValue((string)args![0]!, out var existing)
                        && existing.ExpiresAt > (DateTimeOffset)args[1]!;
                default:
                    return null;
            }
        });
        var refreshTokenRepository = HardeningProxy.Create<IRefreshTokenRepository>((method, args) =>
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
                case nameof(IRefreshTokenRepository.RevokeAllForUserAndClientAsync):
                    var family = refreshTokensByHash.Values
                        .Where(token => token.UserId == (Guid)args![0]! && token.ClientId == (string)args[1]! && !token.IsRevoked)
                        .ToList();
                    family.ForEach(token => token.Revoke());
                    FamilyRevocations++;
                    return family.Count;
                case nameof(IRefreshTokenRepository.RotateAsync):
                    if (!refreshTokensByHash.TryGetValue((string)args![0]!, out var rotating) || rotating.IsRevoked)
                        return null;
                    rotating.Revoke();
                    var replacement = (RefreshToken)args[2]!;
                    refreshTokensByHash[replacement.TokenHash] = replacement;
                    return replacement;
                default:
                    return null;
            }
        });
        var apiResources = HardeningProxy.Create<IApiResourceRepository>((method, args) =>
        {
            if (method.Name != nameof(IApiResourceRepository.FindByAudiencesAsync))
                return null;
            var audiences = ((IEnumerable<string>)args![0]!).ToHashSet(StringComparer.Ordinal);
            return (IReadOnlyList<ApiResource>)[.. resources.Where(resource => audiences.Contains(resource.Audience))];
        });
        var clientService = HardeningProxy.Create<IClientService>((method, args) =>
        {
            if (method.Name != nameof(IClientService.ValidateScopesAsync))
                return null;

            ScopeValidationCalls++;
            if (!clientIsActive)
                return ScopeValidationResult.Failure("invalid_client", "Client not found or inactive.");

            var requestedScopes = ((string)args![1]!)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var unavailableScope = requestedScopes.FirstOrDefault(unavailableClientScopes.Contains);
            if (unavailableScope is not null)
                return ScopeValidationResult.Failure("invalid_scope", $"Scope '{unavailableScope}' is no longer allowed.");

            var resourceScopes = requestedScopes.Where(scope => !OidcScopes.IsStandard(scope)).ToArray();
            foreach (var resourceScope in resourceScopes)
            {
                if (!resources.Any(resource => resource.IsActive
                    && string.Equals(resource.Audience, resourceScope, StringComparison.Ordinal)))
                {
                    return ScopeValidationResult.Failure(
                        "invalid_scope",
                        $"Scope '{resourceScope}' does not correspond to an active resource.");
                }
            }

            if (resourceScopes.Length > 1)
            {
                return ScopeValidationResult.Failure(
                    "invalid_scope",
                    "Requested scopes span multiple audiences. Please request one resource audience per token.");
            }

            return ScopeValidationResult.Success(string.Join(' ', requestedScopes));
        });

        Service = new TokenService(
            refreshTokenRepository,
            blacklistRepository,
            userRepository,
            apiResources,
            clientService,
            Options.Create(new JwtOptions
            {
                Issuer = Issuer,
                DefaultAudience = "default.api",
                AccessTokenLifetimeMinutes = 60,
                RefreshTokenLifetimeMinutes = 60,
            }),
            new HardeningSigningCredentialsProvider(signingKey),
            NullLogger<TokenService>.Instance,
            Policy.Service);
    }

    public TokenService Service { get; }

    public TestSecurityPolicy Policy { get; }

    public int FamilyRevocations { get; private set; }

    public int ScopeValidationCalls { get; private set; }

    public void Add(User user) => users[user.Id] = user;

    public User? FindUser(Guid id) => users.GetValueOrDefault(id);

    public void AddResource(ApiResource resource) => resources.Add(resource);

    public void RevokeClientScope(string scope) => unavailableClientScopes.Add(scope);

    public bool IsRefreshTokenRevoked(string rawToken) =>
        refreshTokensByHash.TryGetValue(RefreshToken.Hash(rawToken), out var token) && token.IsRevoked;

    public RefreshToken GetRefreshToken(string rawToken) =>
        refreshTokensByHash[RefreshToken.Hash(rawToken)];

    public void Dispose() => rsa.Dispose();
}

/// <summary>
/// Minimal <see cref="IAuthenticationService"/> that records sign-outs for controller tests.
/// </summary>
public sealed class RecordingSignOutAuthenticationService : IAuthenticationService
{
    public int SignOutCount { get; private set; }

    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        SignOutCount++;
        return Task.CompletedTask;
    }

    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
}

/// <summary>
/// Client service proxy that answers the authorize-time validation calls with success and records
/// the authentication inputs it receives.
/// </summary>
public static class HardeningClientServices
{
    public static IClientService AcceptingAuthorizeRequests(
        List<ClientAuthenticationInput>? authenticationInputs = null,
        OAuthClient? client = null)
    {
        return HardeningProxy.Create<IClientService>((method, args) => method.Name switch
        {
            nameof(IClientService.ValidateClientRedirectUriAsync) => ClientValidationResult.Success(),
            nameof(IClientService.ValidateClientForAuthorizationAsync) => ClientValidationResult.Success(),
            nameof(IClientService.ValidateScopesAsync) => ScopeValidationResult.Success((string)args![1]!),
            nameof(IClientService.AuthenticateClientAsync) when args![0] is ClientAuthenticationInput input
                => Record(authenticationInputs, input, client),
            _ => null,
        });
    }

    private static ClientAuthenticationResult Record(
        List<ClientAuthenticationInput>? inputs,
        ClientAuthenticationInput input,
        OAuthClient? client)
    {
        inputs?.Add(input);
        return client is null
            ? ClientAuthenticationResult.Failure("invalid_client", "Client not found or inactive.")
            : ClientAuthenticationResult.Success(client);
    }
}
