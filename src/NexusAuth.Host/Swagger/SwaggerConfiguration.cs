using Microsoft.OpenApi;

namespace NexusAuth.Host.Swagger;

internal static class SwaggerConfiguration
{
    internal const string BearerSecuritySchemeName = "Bearer";

    /// <summary>
    /// 注册 Swagger 生成器，并为 SCIM endpoints 提供 Bearer 安全方案。
    /// </summary>
    internal static IServiceCollection AddNexusAuthSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "NexusAuth API",
                Description = "NexusAuth 主机端点，包含 SCIM 2.0 用户供给接口。",
                Version = "v1"
            });

            options.AddSecurityDefinition(BearerSecuritySchemeName, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "SCIM service-principal bearer token."
            });

            options.DocumentFilter<ScimDocumentFilter>();
        });

        return services;
    }

    internal const string EnabledConfigurationKey = "Swagger:Enabled";

    /// <summary>
    /// 暴露 /swagger/v1/swagger.json 与 /swagger Swagger UI。
    /// 中文注释：仅在 Development 环境或显式配置 Swagger:Enabled=true 时启用，生产环境默认不公开接口文档。
    /// </summary>
    internal static IApplicationBuilder UseNexusAuthSwagger(this IApplicationBuilder app)
    {
        var environment = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        if (!IsEnabled(environment, configuration))
            return app;

        app.UseSwagger();
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "NexusAuth API v1"));
        return app;
    }

    internal static bool IsEnabled(IHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration[EnabledConfigurationKey];
        if (bool.TryParse(configured, out var enabled))
            return enabled;

        return environment.IsDevelopment();
    }
}
