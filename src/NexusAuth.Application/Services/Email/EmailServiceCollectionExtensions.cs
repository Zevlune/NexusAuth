using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusAuth.Shared.Email;

namespace NexusAuth.Application.Services.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddNexusAuthEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddNexusAuthSmtp(configuration);
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<EmailVerificationCodeService>();
        return services;
    }
}
