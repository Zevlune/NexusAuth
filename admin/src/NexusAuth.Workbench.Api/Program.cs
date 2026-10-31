using NexusAuth.Workbench.Api.Options;
using Luck.AutoDependencyInjection;
using Luck.AspNetCore.Extensions;
using Luck.Logging.Serilog;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NexusAuth.Extension;
using NexusAuth.Persistence;
using NexusAuth.Workbench.Api;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

try
{
    builder.AddLuckSerilog();
    // 管理接口使用 WorkbenchApiResult；保留 ApiController 默认的请求体/模型校验。

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new SystemTextJsonConvert.DateTimeConverter());
            options.JsonSerializerOptions.Converters.Add(new SystemTextJsonConvert.DateTimeNullConverter());
            options.JsonSerializerOptions.Converters.Add(new SystemTextJsonConvert.DateTimeOffsetConverter());
            options.JsonSerializerOptions.Converters.Add(new SystemTextJsonConvert.DateTimeOffsetNullConverter());
        });
    builder.Services.AddApplication<WorkbenchApiModule>();
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

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "NexusAuth Workbench API", Version = "v1" });
    });

    var app = builder.Build();
    var swaggerEnabled = app.Services.GetRequiredService<IOptions<WorkbenchSwaggerOptions>>().Value.Enabled
        ?? app.Environment.IsDevelopment();

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
    // 登录、登出与配置接口由 SDK 提供；/api/auth/me 保留 Workbench 自身的管理员判定。
    app.MapNexusAuthEndpoints(configure: options =>
    {
        options.LoginRateLimitPolicy = "workbench-login";
        options.MapCurrentUser = false;
    });

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
