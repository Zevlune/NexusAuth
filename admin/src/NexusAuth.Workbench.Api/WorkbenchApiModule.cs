using Luck.AppModule;
using Luck.AutoDependencyInjection;
using Luck.Framework.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Email;
using NexusAuth.Persistence;
using NexusAuth.Workbench.Api.Authorization;

namespace NexusAuth.Workbench.Api;

[DependsOn(
    typeof(AutoDependencyAppModule),
    typeof(EntityFrameworkCoreModule)
)]
public class WorkbenchApiModule : LuckAppModule
{
    public override void ConfigureServices(ConfigureServicesContext context)
    {
        var services = context.Services;
        var configuration = services.GetConfiguration();

        services.AddNexusAuthEmail(configuration);
        ConfigureSecurity(services, configuration);
        services.AddWorkbenchAuthentication(configuration);
        services.AddWorkbenchAuthorization();
        ConfigureBootstrap(services, configuration);

        base.ConfigureServices(context);
    }

    private static void ConfigureBootstrap(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkbenchBootstrapOptions>(configuration.GetSection(WorkbenchBootstrapOptions.SectionName));
        services.AddHostedService<WorkbenchClientCredentialHostedService>();
    }

    private static void ConfigureSecurity(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WorkbenchSecurityOptions>()
            .Bind(configuration.GetSection(WorkbenchSecurityOptions.SectionName))
            .Validate(
                options => WorkbenchSecurityOptions.TryParseCookieSecurePolicy(options.CookieSecurePolicy, out _),
                "Security:CookieSecurePolicy must be SameAsRequest or Always.")
            .ValidateOnStart();
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<WorkbenchSecurityOptions>>((options, security) =>
                WorkbenchSecurityOptions.ConfigureForwardedHeaders(options, security.Value));
        services.AddOptions<WorkbenchSwaggerOptions>()
            .Bind(configuration.GetSection(WorkbenchSwaggerOptions.SectionName))
            .ValidateOnStart();
    }

    public override void ApplicationInitialization(ApplicationContext context)
    {
        var app = context.GetApplicationBuilder();
        app.UseAuthentication();
        app.UseAuthorization();

        base.ApplicationInitialization(context);
    }
}
