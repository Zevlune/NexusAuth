using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.ExternalIdentity;

namespace NexusAuth.Host.TokenEndpoint;

public class RefreshTokenClientStrategyResolver(
    IServiceProvider serviceProvider,
    IOptions<WeChatMiniProgramOptions> weChatOptions) : IRefreshTokenClientStrategyResolver
{
    private readonly WeChatMiniProgramOptions _options = weChatOptions.Value;

    public IRefreshTokenClientStrategy Resolve(string clientId)
    {
        var serviceKey = _options.Enabled && string.Equals(clientId, _options.ClientId, StringComparison.Ordinal)
            ? RefreshTokenClientKeys.WeChatMiniProgram
            : RefreshTokenClientKeys.Confidential;
        return serviceProvider.GetRequiredKeyedService<IRefreshTokenClientStrategy>(serviceKey);
    }
}
