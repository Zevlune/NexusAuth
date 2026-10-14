using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Application.Services.Tokens;

/// <summary>Checks persisted revocation state after cryptographic JWT validation.</summary>
public interface IUserTokenValidator : IScopedDependency
{
    Task<bool> IsActiveAsync(ClaimsPrincipal principal, CancellationToken ct = default);
}
