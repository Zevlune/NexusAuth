using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusAuth.Application.Services.Tokens;

public static class TokenSigningServiceCollectionExtensions
{
    public static IServiceCollection AddNexusAuthTokenSigning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.AddHostedService<TokenSigningCredentialsValidationHostedService>();

        return services;
    }

    public static IServiceCollection AddNexusAuthTokenSigningKeySource<TSource>(this IServiceCollection services)
        where TSource : class, ITokenSigningKeySource
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITokenSigningKeySource, TSource>());
        return services;
    }
}
