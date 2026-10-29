using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.Authorization;
using NexusAuth.Application.Services.OIDC;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Host.IntegrationTests;

internal sealed class ConsentPageTestHarness : IDisposable
{
    public const string ClientId = "web-client";
    public const string RedirectUri = "https://client.example/callback";
    private const string TestUserHeader = "X-Test-User";

    public ConsentPageTestHarness(
        WebApplicationFactory<AppWebModule> factory,
        bool requireConsent = true)
    {
        var testUser = User.Create("consent-user", "Test-password-123!", "Consent User");
        var authorizationClient = OAuthClient.Create(
            ClientId,
            "Consent Test Client",
            redirectUris: [RedirectUri],
            requireConsent: requireConsent);
        UserId = testUser.Id;
        Factory = factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseTestSetting("BootstrapAdmin:Username", string.Empty)
            .UseTestSetting("BootstrapAdmin:Password", string.Empty)
            .ConfigureTestServices(services =>
            {
                services.Remove(services.Single(descriptor =>
                    descriptor.ImplementationType == typeof(BootstrapAdminHostedService)));
                services.RemoveAll<IUserService>();
                services.AddSingleton(HardeningProxy.Create<IUserService>((method, args) =>
                    method.Name == nameof(IUserService.FindByIdAsync)
                    && args![0] is Guid userId
                    && userId == testUser.Id
                        ? testUser
                        : null));
                services.RemoveAll<IClientService>();
                services.AddSingleton(HardeningProxy.Create<IClientService>((method, args) =>
                    method.Name switch
                    {
                        nameof(IClientService.ValidateClientRedirectUriAsync)
                            => ClientValidationResult.Success(),
                        nameof(IClientService.ValidateClientForAuthorizationAsync)
                            => ClientValidationResult.Success(authorizationClient),
                        nameof(IClientService.ValidateScopesAsync)
                            => ScopeValidationResult.Success((string)args![1]!),
                        _ => null,
                    }));
                services.RemoveAll<IAuthorizationService>();
                services.AddSingleton<IAuthorizationService>(AuthorizationService);
                services.RemoveAll<IConsentStore>();
                services.AddSingleton<IConsentStore>(Store);
                services.AddSingleton<IStartupFilter, ConsentTestUserStartupFilter>();
            }));
    }

    public Guid UserId { get; }

    public InMemoryConsentStore Store { get; } = new();

    public RecordingAuthorizationService AuthorizationService { get; } = new();

    public WebApplicationFactory<AppWebModule> Factory { get; }

    public HttpClient CreateClient()
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add(TestUserHeader, UserId.ToString());
        return client;
    }

    public void Dispose() => Factory.Dispose();
}

internal sealed class RecordingAuthorizationService : IAuthorizationService
{
    public List<GeneratedCodeRequest> GeneratedCodes { get; } = [];

    public Task<string> GenerateCodeAsync(
        Guid userId,
        string clientId,
        string redirectUri,
        string scope,
        string? codeChallenge = null,
        string? codeChallengeMethod = null,
        string? nonce = null,
        string? claimsJson = null,
        DateTimeOffset? authenticatedAt = null,
        string? acr = null,
        string? amr = null,
        CancellationToken ct = default)
    {
        GeneratedCodes.Add(new GeneratedCodeRequest(
            userId,
            clientId,
            redirectUri,
            scope,
            codeChallenge,
            codeChallengeMethod,
            nonce,
            claimsJson,
            authenticatedAt,
            acr,
            amr));
        return Task.FromResult("consent-code");
    }

    public Task<AuthorizationCodeResult> ValidateAndConsumeCodeAsync(
        string code,
        string clientId,
        string redirectUri,
        string? codeVerifier = null,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ClientCredentialsResult> ValidateClientCredentialsAsync(
        ClientAuthenticationInput authentication,
        string scope,
        CancellationToken ct = default) => throw new NotSupportedException();

    public OidcRequestedClaims ParseRequestedClaims(string? claimsJson) => OidcRequestedClaims.Empty;
}

internal sealed class InMemoryConsentStore : IConsentStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, StoredConsentRequest> requests = new(StringComparer.Ordinal);
    private readonly List<ConsentGrantRecord> grants = [];
    private int nextId;

    public int CreateCalls { get; private set; }

    public int ApproveCalls { get; private set; }

    public int HasGrantCalls { get; private set; }

    public IReadOnlyList<ConsentGrantRecord> Grants
    {
        get
        {
            lock (gate)
                return [.. grants];
        }
    }

    public Task<string> CreateRequestAsync(Guid userId, string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            CreateCalls++;
            var id = $"consent-{++nextId}";
            requests.Add(id, new StoredConsentRequest(id, userId, query));
            return Task.FromResult(id);
        }
    }

    public Task<ConsentRequestState?> GetRequestAsync(string id, Guid userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var request = Find(id, userId);
            return Task.FromResult(request is null || request.Status == InMemoryConsentStatus.Consumed
                ? null
                : new ConsentRequestState(request.Query, request.Remember));
        }
    }

    public Task<bool> ApproveRequestAsync(string id, Guid userId, bool remember, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            ApproveCalls++;
            var request = Find(id, userId);
            if (request is null || request.Status != InMemoryConsentStatus.Pending)
                return Task.FromResult(false);

            request.Status = InMemoryConsentStatus.Approved;
            request.Remember = remember;
            return Task.FromResult(true);
        }
    }

    public Task<ConsentRequestState?> ConsumeApprovedRequestAsync(string id, Guid userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var request = Find(id, userId);
            if (request is null || request.Status != InMemoryConsentStatus.Approved)
                return Task.FromResult<ConsentRequestState?>(null);

            request.Status = InMemoryConsentStatus.Consumed;
            return Task.FromResult<ConsentRequestState?>(
                new ConsentRequestState(request.Query, request.Remember));
        }
    }

    public Task<bool> HasGrantAsync(
        Guid userId,
        string clientId,
        string scope,
        string? claims,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedScope = NormalizeScope(scope);
        lock (gate)
        {
            HasGrantCalls++;
            return Task.FromResult(grants.Any(grant =>
                grant.UserId == userId
                && grant.ClientId == clientId
                && grant.Scope == normalizedScope
                && grant.Claims == claims));
        }
    }

    public Task SaveGrantAsync(
        Guid userId,
        string clientId,
        string scope,
        string? claims,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedScope = NormalizeScope(scope);
        lock (gate)
        {
            if (!grants.Any(grant =>
                    grant.UserId == userId
                    && grant.ClientId == clientId
                    && grant.Scope == normalizedScope
                    && grant.Claims == claims))
            {
                grants.Add(new ConsentGrantRecord(userId, clientId, normalizedScope, claims));
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConsentGrantState>> ListGrantsAsync(
        Guid userId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<ConsentGrantState> result = grants
                .Where(grant => grant.UserId == userId)
                .OrderBy(grant => grant.ClientId, StringComparer.Ordinal)
                .ThenBy(grant => grant.Scope, StringComparer.Ordinal)
                .ThenBy(grant => grant.Claims, StringComparer.Ordinal)
                .Select(grant => new ConsentGrantState(grant.ClientId, grant.Scope, grant.Claims))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<int> RevokeGrantsAsync(
        Guid userId,
        string clientId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var revoked = grants.RemoveAll(grant =>
                grant.UserId == userId && grant.ClientId == clientId);
            return Task.FromResult(revoked);
        }
    }

    public StoredConsentRequestSnapshot GetRequest(string id)
    {
        lock (gate)
            return requests.TryGetValue(id, out var request)
                ? request.ToSnapshot()
                : throw new KeyNotFoundException($"Consent request '{id}' was not created.");
    }

    public InMemoryConsentStatus GetStatus(string id)
    {
        lock (gate)
            return requests.TryGetValue(id, out var request)
                ? request.Status
                : throw new KeyNotFoundException($"Consent request '{id}' was not created.");
    }

    private StoredConsentRequest? Find(string id, Guid userId)
    {
        return requests.TryGetValue(id, out var request) && request.UserId == userId
            ? request
            : null;
    }

    private static string NormalizeScope(string scope) =>
        string.Join(
            ' ',
            scope
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal));

    private sealed class StoredConsentRequest(string id, Guid userId, string query)
    {
        public string Id { get; } = id;

        public Guid UserId { get; } = userId;

        public string Query { get; } = query;

        public bool Remember { get; set; }

        public InMemoryConsentStatus Status { get; set; } = InMemoryConsentStatus.Pending;

        public StoredConsentRequestSnapshot ToSnapshot() => new(Id, UserId, Query, Remember, Status);
    }
}

internal enum InMemoryConsentStatus
{
    Pending,
    Approved,
    Consumed,
}

internal sealed record StoredConsentRequestSnapshot(
    string Id,
    Guid UserId,
    string Query,
    bool Remember,
    InMemoryConsentStatus Status);

internal sealed record ConsentGrantRecord(Guid UserId, string ClientId, string Scope, string? Claims);

internal sealed record GeneratedCodeRequest(
    Guid UserId,
    string ClientId,
    string RedirectUri,
    string Scope,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? Nonce,
    string? Claims,
    DateTimeOffset? AuthenticatedAt,
    string? Acr,
    string? Amr);

internal sealed class ConsentTestUserStartupFilter : IStartupFilter
{
    private const string TestUserHeader = "X-Test-User";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(TestUserHeader, out var userId))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, "consent-user"),
                    new Claim("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
                ], "Test"));
            }

            await nextMiddleware();
        });
        next(app);
    };
}
