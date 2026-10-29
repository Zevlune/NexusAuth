using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Domain.Repositories;

public interface IAuthenticationDataCleanupRepository : IScopedDependency
{
    Task<AuthenticationDataCleanupResult> CleanupAsync(
        DateTimeOffset now,
        DateTimeOffset expiredAtOrBefore,
        DateTimeOffset loginAuditOccurredAtOrBefore,
        int batchSize,
        int maxBatchesPerType,
        CancellationToken ct = default);
}

public sealed record AuthenticationDataCleanupResult(
    int AuthorizationCodes,
    int RefreshTokens,
    int DeviceAuthorizations,
    int TokenBlacklistEntries,
    int WebAuthnChallenges,
    int SsoSessions,
    int LoginAuditLogs,
    int ConsentRequests = 0)
{
    public int Total => AuthorizationCodes
        + RefreshTokens
        + DeviceAuthorizations
        + TokenBlacklistEntries
        + WebAuthnChallenges
        + SsoSessions
        + LoginAuditLogs
        + ConsentRequests;
}
