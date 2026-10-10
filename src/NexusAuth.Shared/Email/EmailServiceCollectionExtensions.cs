using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NexusAuth.Shared.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddNexusAuthSmtp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>();
        services.TryAddTransient<ISmtpTransport, MailKitSmtpTransport>();
        services.TryAddTransient<IEmailSender, SmtpEmailSender>();
        return services;
    }
}
