using Luck.Framework.Infrastructure.DependencyInjectionModule;
using NexusAuth.Domain.AggregateRoots.Users;

namespace NexusAuth.Application.Services.Security;

public interface ISecurityPolicyService : IScopedDependency
{
    PolicyCheckResult CheckClient(string clientId);

    PolicyCheckResult CheckUser(User user);
}
