using Luck.Framework.Infrastructure.DependencyInjectionModule;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace NexusAuth.Workbench.Api;

public interface IWorkbenchCookieAuthenticationEvents : IScopedDependency
{
    Task ValidatePrincipal(CookieValidatePrincipalContext context);
}
