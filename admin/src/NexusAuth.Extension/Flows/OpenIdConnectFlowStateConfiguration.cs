using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

internal static class OpenIdConnectFlowStateProvider
{
    internal const string InMemory = "InMemory";
    internal const string Redis = "Redis";
}

internal sealed class ConfiguredFlowStateStore : IFlowStateStore
{
    private readonly IFlowStateStore store;

    public ConfiguredFlowStateStore(
        IServiceProvider services,
        IOptions<OpenIdConnectFlowStateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        store = services.GetRequiredKeyedService<IFlowStateStore>(
            options.Value.Provider);
    }

    public Task AddAsync(
        string state,
        FlowState flowState,
        CancellationToken ct = default) =>
        store.AddAsync(state, flowState, ct);

    public Task<FlowState?> TryTakeAsync(
        string state,
        string browserBinding,
        CancellationToken ct = default) =>
        store.TryTakeAsync(state, browserBinding, ct);

    public Task RemoveAsync(string state, CancellationToken ct = default) =>
        store.RemoveAsync(state, ct);
}

internal sealed class OpenIdConnectFlowStateOptionsValidator
    : IValidateOptions<OpenIdConnectFlowStateOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        OpenIdConnectFlowStateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.Provider is not (
            OpenIdConnectFlowStateProvider.InMemory
            or OpenIdConnectFlowStateProvider.Redis))
        {
            failures.Add("Auth:FlowState Provider must be exactly InMemory or Redis.");
        }

        if (options.Capacity is < 1 or > 100_000)
        {
            failures.Add("Auth:FlowState Capacity must be between 1 and 100000.");
        }

        if (options.Lifetime <= TimeSpan.Zero
            || options.Lifetime > TimeSpan.FromHours(1))
        {
            failures.Add(
                "Auth:FlowState Lifetime must be positive and at most one hour.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

internal sealed class RedisFlowStateOptionsValidator(
    IOptions<OpenIdConnectFlowStateOptions> flowOptions,
    bool configurationRequired)
    : IValidateOptions<RedisFlowStateOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        RedisFlowStateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (flowOptions.Value.Provider != OpenIdConnectFlowStateProvider.Redis)
            return ValidateOptionsResult.Success;

        var error = options.GetValidationError(configurationRequired);
        return error is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}
