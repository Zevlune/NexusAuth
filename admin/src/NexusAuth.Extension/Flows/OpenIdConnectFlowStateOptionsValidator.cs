using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

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
