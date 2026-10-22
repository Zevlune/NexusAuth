using NexusAuth.Shared.DependencyInjection;

namespace NexusAuth.Host.TokenEndpoint;

public static class TokenEndpointServiceCollectionExtensions
{
    public static IServiceCollection AddTokenGrantStrategies(this IServiceCollection services)
    {
        services.AddScopeKeyedServices(typeof(ITokenGrantStrategy).Assembly);
        return services;
    }
}
