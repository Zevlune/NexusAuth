using Luck.Framework.Infrastructure.DependencyInjectionModule;
using MimeKit;

namespace NexusAuth.Shared.Email;

public interface ISmtpTransport : ITransientDependency
{
    Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct = default);
}
