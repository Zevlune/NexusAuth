using Luck.AutoDependencyInjection;
using Luck.Logging.Serilog;
using Microsoft.Extensions.Configuration;
using NexusAuth.Host;
using NexusAuth.Host.Controllers;
using NexusAuth.Host.Swagger;
using NexusAuth.Application.Services.Email;

var builder = WebApplication.CreateBuilder(args);
var listenUrls = builder.Configuration["urls"];
var appSettingsConfiguration = new ConfigurationBuilder()
    .SetFileProvider(builder.Environment.ContentRootFileProvider)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .Build();
builder.Configuration.Sources.Clear();
builder.Configuration.AddConfiguration(appSettingsConfiguration);
if (!string.IsNullOrWhiteSpace(listenUrls))
    builder.WebHost.UseUrls(listenUrls);
builder.AddLuckSerilog();

try
{
    builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        manager.FeatureProviders.Add(new ScimControllerFeatureProvider(builder.Configuration)));
    builder.Services.AddRazorPages();
    builder.Services.AddHttpContextAccessor();
    builder.Services.Configure<BootstrapAdminOptions>(appSettingsConfiguration.GetSection("BootstrapAdmin"));
    builder.Services.AddNexusAuthEmail(appSettingsConfiguration);
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(
        options => NexusAuth.Host.Authentication.ClientIpConfiguration.Configure(options, builder.Configuration));
    builder.Services.AddApplication<AppWebModule>();
    builder.Services.AddNexusAuthSwagger();

    var app = builder.Build();

    app.UseForwardedHeaders();

    app.UseStaticFiles();
    app.InitializeApplication();
    app.UseLuckRequestLogContext();
    app.UseNexusAuthSwagger();
    app.MapControllers();
    app.MapRazorPages();

    app.Run();
}
catch (Exception exception)
{
    LoggingExtensions.LogStartupFailure(exception);
    throw;
}
finally
{
    LoggingExtensions.CloseAndFlush();
}
