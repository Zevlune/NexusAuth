using Luck.AutoDependencyInjection;
using Luck.Framework.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace NexusAuth.Host.IntegrationTests;

internal static class AutoDependencyServices
{
    public static IServiceCollection Create()
    {
        var services = new ServiceCollection();
        new AutoDependencyAppModule().ConfigureServices(new ConfigureServicesContext(services));
        return services;
    }
}
