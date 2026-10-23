using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Host.Authentication;

public interface ILoginFlowStateProtector : ISingletonDependency
{
    string Protect(PendingLoginFlowState state, TimeSpan lifetime);
    bool TryUnprotect(string? protectedState, out PendingLoginFlowState? state);
}
