using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Application.Services.Email;

public interface IEmailVerificationCodeService : ISingletonDependency
{
    bool IsAvailable { get; }
    int CooldownSeconds { get; }
    int LifetimeMinutes { get; }

    Task SendAsync(
        string email,
        string purpose,
        string binding,
        CancellationToken cancellationToken = default);
    bool TryVerify(string email, string purpose, string binding, string code);
    bool TryConsume(string email, string purpose, string binding, string code);
}
