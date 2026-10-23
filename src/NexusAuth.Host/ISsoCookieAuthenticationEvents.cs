using Luck.Framework.Infrastructure.DependencyInjectionModule;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace NexusAuth.Host;

public interface ISsoCookieAuthenticationEvents : IScopedDependency
{
    Task ValidatePrincipal(CookieValidatePrincipalContext context);
}
