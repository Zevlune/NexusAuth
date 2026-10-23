using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Host.TokenEndpoint;

public interface IRefreshTokenClientStrategyResolver : IScopedDependency
{
    IRefreshTokenClientStrategy Resolve(string clientId);
}
