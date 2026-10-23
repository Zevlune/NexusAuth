using NexusAuth.Application.Clients;

namespace NexusAuth.Application.Clients.Authentication;

[IgnoreDependency]
public interface IClientAuthenticationStrategy : IScopedDependency
{
    Task<ClientAuthenticationStrategyResult> AuthenticateAsync(
        ClientAuthenticationInput input,
        OAuthClient client,
        CancellationToken ct = default);
}
