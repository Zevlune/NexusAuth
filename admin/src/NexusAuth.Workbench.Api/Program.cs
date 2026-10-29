using Luck.AutoDependencyInjection;
using Luck.AspNetCore.Extensions;
using Luck.Logging.Serilog;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexusAuth.Persistence;
using NexusAuth.Workbench.Api;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddWorkbenchEnvironmentVariables();

try
{
    builder.AddLuckSerilog();
    builder.Services.AddApiResult();

    builder.Services.AddControllers()
        .ConfigureApplicationPartManager(manager =>
        {
            if (builder.Configuration.IsWorkbenchGatewayEnabled())
                manager.FeatureProviders.Add(new GatewayControllerFeatureProvider());
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new SystemTextJsonConvert.DateTimeConverter());
            options.JsonSerializerOptions.Converters.Add(new SystemTextJsonConvert.DateTimeNullConverter());
            options.JsonSerializerOptions.Converters.Add(new SystemTextJsonConvert.DateTimeOffsetConverter());
            options.JsonSerializerOptions.Converters.Add(new SystemTextJsonConvert.DateTimeOffsetNullConverter());
        });
    builder.Services.AddApplication<WorkbenchApiModule>();
    if (builder.Configuration.IsWorkbenchGatewayEnabled())
        builder.Services.RemoveAll<IWorkbenchCookieAuthenticationEvents>();
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("workbench-login", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = 20,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    Window = TimeSpan.FromMinutes(1),
                }));
    });

    var swaggerEnabled = builder.Configuration.IsWorkbenchSwaggerEnabled(builder.Environment);
    if (swaggerEnabled)
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new() { Title = "NexusAuth Workbench API", Version = "v1" });
        });
    }

    var app = builder.Build();

    var security = app.Services.GetRequiredService<IOptions<WorkbenchSecurityOptions>>().Value;
    if (security.ForwardedHeadersEnabled)
        app.UseForwardedHeaders();
    app.UseRouting();
    app.Use(static (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/signin-oidc"))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
        }

        return next(context);
    });
    app.InitializeApplication();
    app.UseLuckRequestLogContext();
    app.UseRateLimiter();
    if (swaggerEnabled)
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "NexusAuth Workbench API v1"));
    }

    app.MapControllers();

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

public partial class Program;
