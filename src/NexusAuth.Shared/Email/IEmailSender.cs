using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Shared.Email;

public interface IEmailSender : ITransientDependency
{
    Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default);
}
