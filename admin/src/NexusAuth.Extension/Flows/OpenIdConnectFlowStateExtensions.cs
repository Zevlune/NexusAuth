using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusAuth.Extension;

/// <summary>Registers the dependencies shared by OpenID Connect flow-state providers.</summary>
public static class OpenIdConnectFlowStateExtensions
{
    /// <summary>Adds flow-state options, the application clock, and the scoped flow service.</summary>
    public static OpenIdConnectFlowStateBuilder AddOpenIdConnectFlowState(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OpenIdConnectFlowStateOptions>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddScoped<OpenIdConnectFlowState>();
        return new OpenIdConnectFlowStateBuilder(services);
    }
}
