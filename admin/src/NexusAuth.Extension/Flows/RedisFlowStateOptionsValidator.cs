using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

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
