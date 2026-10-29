using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

internal sealed class MetadataWarmupService : BackgroundService
{
    private const string LoggerCategory =
        "NexusAuth.Extension.OpenIdConnectMetadataWarmupExtensions.MetadataWarmupService";

    private readonly IAuthenticationSchemeProvider schemes;
    private readonly IOptionsMonitor<OpenIdConnectOptions> options;
    private readonly ILogger logger;

    public MetadataWarmupService(
        IAuthenticationSchemeProvider schemes,
        IOptionsMonitor<OpenIdConnectOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.schemes = schemes;
        this.options = options;
        logger = loggerFactory.CreateLogger(LoggerCategory);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registered = await schemes.GetAllSchemesAsync();
        // 各方案独立执行，某个 Provider 不可用时不阻塞其他方案或应用启动。
        await Task.WhenAll(registered
            .Where(scheme => typeof(OpenIdConnectHandler).IsAssignableFrom(scheme.HandlerType))
            .Select(scheme => WarmupAsync(scheme.Name, stoppingToken)));
    }

    private async Task WarmupAsync(string scheme, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var oidc = options.Get(scheme);
                // 显式 Configuration 已经由调用方提供，无需再发起发现请求。
                if (oidc.Configuration is not null)
                    return;
                await oidc.ConfigurationManager!.GetConfigurationAsync(stoppingToken).WaitAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // 不输出远端响应或配置内容，避免将令牌、凭据写入日志。
                logger.LogWarning(
                    "OIDC metadata warmup failed for {Scheme}; retrying in 30 seconds. FailureType={FailureType}",
                    scheme,
                    exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}
