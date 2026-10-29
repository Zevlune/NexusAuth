using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.DataRetention;

namespace NexusAuth.Host.Maintenance;

public sealed class AuthenticationDataCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthenticationDataRetentionOptions> options,
    ILogger<AuthenticationDataCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Authentication data retention cleanup is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.SweepIntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var cleanup = scope.ServiceProvider.GetRequiredService<IAuthenticationDataCleanupService>();
                    var result = await cleanup.CleanupAsync(stoppingToken);
                    logger.LogInformation(
                        "Authentication data cleanup deleted {Total} rows: authorization codes {AuthorizationCodes}, " +
                        "refresh tokens {RefreshTokens}, device authorizations {DeviceAuthorizations}, " +
                        "token blacklist entries {TokenBlacklistEntries}, WebAuthn challenges {WebAuthnChallenges}, " +
                        "SSO sessions {SsoSessions}, login audit logs {LoginAuditLogs}, consent requests {ConsentRequests}.",
                        result.Total,
                        result.AuthorizationCodes,
                        result.RefreshTokens,
                        result.DeviceAuthorizations,
                        result.TokenBlacklistEntries,
                        result.WebAuthnChallenges,
                        result.SsoSessions,
                        result.LoginAuditLogs,
                        result.ConsentRequests);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Authentication data retention cleanup failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
