using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NexusAuth.Host.IntegrationTests;

internal static class HostTestSettingsExtensions
{
    public static IWebHostBuilder UseTestSetting(this IWebHostBuilder builder, string key, string value) =>
        builder.ConfigureTestServices(services =>
        {
            var configuration = services.BuildServiceProvider().GetRequiredService<IConfiguration>();
            configuration[key] = value;
        });
}
