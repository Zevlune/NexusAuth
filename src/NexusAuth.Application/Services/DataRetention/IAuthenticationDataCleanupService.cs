using NexusAuth.Domain.Repositories;

namespace NexusAuth.Application.Services.DataRetention;

public interface IAuthenticationDataCleanupService
{
    Task<AuthenticationDataCleanupResult> CleanupAsync(CancellationToken ct = default);
}
