using Luck.Framework.Infrastructure.DependencyInjectionModule;
using NexusAuth.Domain.AggregateRoots.Users;

namespace NexusAuth.Application.Services.Email;

public interface IEmailAccountService : IScopedDependency
{
    bool IsAvailable { get; }
    int CooldownSeconds { get; }
    int LifetimeMinutes { get; }

    Task SendPublicAsync(string email, string purpose, CancellationToken ct = default);
    Task<User?> AuthenticateAsync(string email, string code, CancellationToken ct = default);
    Task SendVerificationAsync(
        Guid id,
        string email,
        string password,
        string? otp,
        CancellationToken ct = default);
    Task UnbindEmailAsync(Guid id, string password, string? otp, CancellationToken ct = default);
    Task VerifyEmailAsync(Guid id, string email, string code, CancellationToken ct = default);
    Task ResetPasswordAsync(
        string email,
        string code,
        string password,
        string? otp,
        CancellationToken ct = default);
}
