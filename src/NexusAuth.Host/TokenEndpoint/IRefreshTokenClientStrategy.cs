using Luck.Framework.Infrastructure.DependencyInjectionModule;
using NexusAuth.Application.Clients;
using NexusAuth.Domain.AggregateRoots.OAuthClients;

namespace NexusAuth.Host.TokenEndpoint;

[IgnoreDependency]
public interface IRefreshTokenClientStrategy : IScopedDependency
{
    Task<ClientAuthenticationResult> AuthenticateAsync(TokenGrantContext context, ClientAuthenticationInput input, CancellationToken ct);
    bool IsGrantAllowed(OAuthClient? client);
}
