using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NexusAuth.Shared.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddNexusAuthSmtp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.SectionName))
            .ValidateOnStart();
        return services;
    }
}
