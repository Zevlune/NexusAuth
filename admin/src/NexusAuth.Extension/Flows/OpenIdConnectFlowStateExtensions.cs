using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>Registers the dependencies shared by OpenID Connect flow-state providers.</summary>
public static class OpenIdConnectFlowStateExtensions
{
    /// <summary>Adds flow-state options, the application clock, and the scoped flow service.</summary>
    public static OpenIdConnectFlowStateBuilder AddOpenIdConnectFlowState(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OpenIdConnectFlowStateOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<OpenIdConnectFlowStateOptions>,
                OpenIdConnectFlowStateOptionsValidator>());
        services.AddOptions<RedisFlowStateOptions>().ValidateOnStart();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddScoped<OpenIdConnectFlowState>();
        services.TryAddScoped<OpenIdConnectFlowStateEvents>();
        return new OpenIdConnectFlowStateBuilder(services);
    }
}
