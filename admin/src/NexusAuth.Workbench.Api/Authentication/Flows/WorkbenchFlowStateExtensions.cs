using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusAuth.Workbench.Api;

/// <summary>Registers the dependencies shared by Workbench flow-state providers.</summary>
public static class WorkbenchFlowStateExtensions
{
    /// <summary>Adds flow-state options and the application clock, then returns a provider builder.</summary>
    public static WorkbenchFlowStateBuilder AddWorkbenchFlowState(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<WorkbenchFlowStateOptions>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        return new WorkbenchFlowStateBuilder(services);
    }
}
