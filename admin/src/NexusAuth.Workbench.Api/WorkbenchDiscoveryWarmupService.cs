using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace NexusAuth.Workbench.Api;

/// <summary>Primes the standard metadata/key cache without coupling startup to Provider uptime.</summary>
internal sealed class WorkbenchDiscoveryWarmupService(
    IConfigurationManager<OpenIdConnectConfiguration> metadata,
    ILogger<WorkbenchDiscoveryWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await metadata.GetConfigurationAsync(stoppingToken).WaitAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning("OIDC metadata warmup failed; retrying in 30 seconds. FailureType={FailureType}", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}
