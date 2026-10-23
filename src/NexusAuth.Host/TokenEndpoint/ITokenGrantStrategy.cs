using Luck.Framework.Infrastructure.DependencyInjectionModule;
using Microsoft.AspNetCore.Mvc;

namespace NexusAuth.Host.TokenEndpoint;

[IgnoreDependency]
public interface ITokenGrantStrategy : IScopedDependency
{
    Task<IActionResult> ExecuteAsync(TokenGrantContext context, CancellationToken ct);
}
