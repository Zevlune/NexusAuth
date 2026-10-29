using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Domain.Repositories;

public interface IConsentStore : IScopedDependency
{
    Task<string> CreateRequestAsync(Guid userId, string query, CancellationToken ct);

    Task<ConsentRequestState?> GetRequestAsync(string id, Guid userId, CancellationToken ct);

    Task<bool> ApproveRequestAsync(string id, Guid userId, bool remember, CancellationToken ct);

    Task<ConsentRequestState?> ConsumeApprovedRequestAsync(string id, Guid userId, CancellationToken ct);

    Task<bool> HasGrantAsync(
        Guid userId,
        string clientId,
        string scope,
        string? claims,
        CancellationToken ct);

    Task SaveGrantAsync(
        Guid userId,
        string clientId,
        string scope,
        string? claims,
        CancellationToken ct);

    Task<IReadOnlyList<ConsentGrantState>> ListGrantsAsync(
        Guid userId,
        CancellationToken ct);

    Task<int> RevokeGrantsAsync(
        Guid userId,
        string clientId,
        CancellationToken ct);
}

public sealed record ConsentRequestState(string Query, bool Remember);

public sealed record ConsentGrantState(string ClientId, string Scope, string? Claims);
