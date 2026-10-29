using Microsoft.Extensions.Options;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Application.Services.DataRetention;

public sealed class AuthenticationDataCleanupService(
    IAuthenticationDataCleanupRepository repository,
    IOptions<AuthenticationDataRetentionOptions> options,
    TimeProvider timeProvider) : IAuthenticationDataCleanupService
{
    public Task<AuthenticationDataCleanupResult> CleanupAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();

        return repository.CleanupAsync(
            now,
            now.AddDays(-settings.ExpiredDataRetentionDays),
            now.AddDays(-settings.LoginAuditRetentionDays),
            settings.CleanupBatchSize,
            settings.MaxBatchesPerTypePerSweep,
            ct);
    }
}
