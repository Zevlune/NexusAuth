namespace NexusAuth.Application.Services.Tokens;

internal class TokenSigningCredentialsValidationHostedService : IHostedService
{
    public TokenSigningCredentialsValidationHostedService(
        ITokenSigningCredentialsProvider signingCredentialsProvider)
    {
        ArgumentNullException.ThrowIfNull(signingCredentialsProvider);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
