using System.Security.Claims;
using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Workbench.Api.Authorization;

/// <summary>
/// 根据当前用户与请求判断 Workbench 访问权限。
/// 实现负责账号校验、权限来源、规则匹配及结果日志，handler 仅转交授权结果。
/// </summary>
public interface IWorkbenchPermissionService : IScopedDependency
{
    Task<bool> HasPermissionAsync(
        ClaimsPrincipal user,
        HttpContext? httpContext,
        CancellationToken ct = default);
}
