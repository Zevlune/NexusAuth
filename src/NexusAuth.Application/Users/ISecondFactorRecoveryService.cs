using NexusAuth.Application.Services.Security;

namespace NexusAuth.Application.Users;

/// <summary>
/// Generates one-time recovery codes and performs an audited administrator
/// reset of a user's second factors.
/// </summary>
public interface ISecondFactorRecoveryService : IScopedDependency
{
    /// <summary>
    /// Replaces all existing recovery codes. The returned clear-text values
    /// are the only copy of the codes; callers must show or export them once.
    /// </summary>
    Task<IReadOnlyList<string>> GenerateCodesAsync(Guid userId, CancellationToken ct = default);

    Task<bool> RedeemAsync(Guid userId, string? code, CancellationToken ct = default);

    Task ResetAsync(Guid userId, Guid actorId, string reason, CancellationToken ct = default);

}
