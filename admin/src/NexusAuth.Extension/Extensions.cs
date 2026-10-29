using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace NexusAuth.Extension;

/// <summary>Registers the NexusAuth backchannel protocol client.</summary>
public static class NexusAuthServiceCollectionExtensions
{
    /// <summary>Adds the scoped backchannel client and shared OpenID Connect configuration manager.</summary>
    public static IServiceCollection AddNexusAuth(
        this IServiceCollection services,
        Action<NexusAuthClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<NexusAuthClientOptions>()
            .Configure(configure)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<NexusAuthClientOptions>, NexusAuthClientOptionsValidator>());

        services.AddHttpClient(NexusAuthClient.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(CreateConfigurationManager);
        services.TryAddScoped<INexusAuthClient, NexusAuthClient>();

        return services;
    }

    private static ConfigurationManager<OpenIdConnectConfiguration> CreateConfigurationManager(
        IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<NexusAuthClientOptions>>().Value;
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var metadataAddress = $"{options.Authority.TrimEnd('/')}/.well-known/openid-configuration";

        return new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new NexusAuthConfigurationRetriever(options),
            new NexusAuthDocumentRetriever(httpClientFactory, options));
    }
}
