using Luck.EntityFrameworkCore.DbContexts;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Persistence.Repositories;

public sealed class AuthenticationDataCleanupRepository(IUnitOfWork unitOfWork)
    : IAuthenticationDataCleanupRepository
{
    private readonly LuckDbContextBase _dbContext = unitOfWork.GetLuckDbContext() as LuckDbContextBase
        ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");

    public async Task<AuthenticationDataCleanupResult> CleanupAsync(
        DateTimeOffset now,
        DateTimeOffset expiredAtOrBefore,
        DateTimeOffset loginAuditOccurredAtOrBefore,
        int batchSize,
        int maxBatchesPerType,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchesPerType, 1);

        var authorizationCodes = await DeleteInBatchesAsync(
            _dbContext.Set<AuthorizationCode>()
                .Where(item => item.ExpiresAt <= expiredAtOrBefore)
                .OrderBy(item => item.ExpiresAt),
            batchSize,
            maxBatchesPerType,
            ct);
        var refreshTokenSet = _dbContext.Set<RefreshToken>();
        var activeRefreshTokens = refreshTokenSet
            .Where(item => !item.IsRevoked && item.ExpiresAt > now);
        var refreshTokens = await DeleteInBatchesAsync(
            refreshTokenSet
                .Where(item => item.ExpiresAt <= expiredAtOrBefore
                    && !(item.IsRevoked
                        && activeRefreshTokens.Any(active => active.UserId == item.UserId
                            && active.ClientId == item.ClientId)))
                .OrderBy(item => item.ExpiresAt),
            batchSize,
            maxBatchesPerType,
            ct);
        var deviceAuthorizations = await DeleteInBatchesAsync(
            _dbContext.Set<DeviceAuthorization>()
                .Where(item => item.ExpiresAt <= expiredAtOrBefore)
                .OrderBy(item => item.ExpiresAt),
            batchSize,
            maxBatchesPerType,
            ct);
        var tokenBlacklistEntries = await DeleteInBatchesAsync(
            _dbContext.Set<TokenBlacklistEntry>()
                .Where(item => item.ExpiresAt <= expiredAtOrBefore
                    && (item.TokenType != RefreshToken.RotationMarkerTokenType
                        || !refreshTokenSet.Any(rotated => item.Jti
                                == RefreshToken.RotationMarkerPrefix + rotated.TokenHash
                            && activeRefreshTokens.Any(active => active.UserId == rotated.UserId
                                && active.ClientId == rotated.ClientId))))
                .OrderBy(item => item.ExpiresAt),
            batchSize,
            maxBatchesPerType,
            ct);
        var webAuthnChallenges = await DeleteInBatchesAsync(
            _dbContext.Set<WebAuthnChallenge>()
                .Where(item => item.ExpiresAt <= expiredAtOrBefore)
                .OrderBy(item => item.ExpiresAt),
            batchSize,
            maxBatchesPerType,
            ct);
        var ssoSessions = await DeleteInBatchesAsync(
            _dbContext.Set<SsoSession>()
                .Where(item => item.ExpiresAt <= expiredAtOrBefore
                    || (item.RevokedAt != null && item.RevokedAt <= expiredAtOrBefore))
                .OrderBy(item => item.ExpiresAt),
            batchSize,
            maxBatchesPerType,
            ct);
        var loginAuditLogs = await DeleteInBatchesAsync(
            _dbContext.Set<LoginAuditLog>()
                .Where(item => item.OccurredAt <= loginAuditOccurredAtOrBefore)
                .OrderBy(item => item.OccurredAt),
            batchSize,
            maxBatchesPerType,
            ct);
        var consentRequests = await DeleteInBatchesAsync(
            _dbContext.Set<ConsentRequest>()
                .Where(item => item.ExpiresAt <= expiredAtOrBefore)
                .OrderBy(item => item.ExpiresAt),
            batchSize,
            maxBatchesPerType,
            ct);

        return new AuthenticationDataCleanupResult(
            authorizationCodes,
            refreshTokens,
            deviceAuthorizations,
            tokenBlacklistEntries,
            webAuthnChallenges,
            ssoSessions,
            loginAuditLogs,
            consentRequests);
    }

    private static async Task<int> DeleteInBatchesAsync<TEntity>(
        IQueryable<TEntity> query,
        int batchSize,
        int maxBatches,
        CancellationToken ct)
        where TEntity : class
    {
        var total = 0;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            var deleted = await query.Take(batchSize).ExecuteDeleteAsync(ct);
            total += deleted;
            if (deleted < batchSize)
                break;
        }

        return total;
    }
}
