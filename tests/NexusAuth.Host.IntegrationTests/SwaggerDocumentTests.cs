using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NexusAuth.Shared.Email;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class SwaggerDocumentTests : IClassFixture<SwaggerDocumentFactory>
{
    private readonly SwaggerDocumentFactory factory;

    public SwaggerDocumentTests(SwaggerDocumentFactory factory) => this.factory = factory;

    [Fact]
    public void Bootstrap_admin_ignores_non_appsettings_configuration()
    {
        using var overriddenFactory = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BootstrapAdmin:Username"] = "ignored",
                ["BootstrapAdmin:Password"] = "ignored"
            })));
        var options = overriddenFactory.Services.GetRequiredService<IOptions<BootstrapAdminOptions>>().Value;
        Assert.True(string.IsNullOrEmpty(options.Username));
        Assert.True(string.IsNullOrEmpty(options.Password));
    }

    [Fact]
    public void Smtp_ignores_non_appsettings_configuration()
    {
        using var overriddenFactory = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Enabled"] = "true",
                ["Smtp:Host"] = "ignored.example.com",
                ["Smtp:Password"] = "ignored"
            })));
        var options = overriddenFactory.Services.GetRequiredService<IOptions<SmtpOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.Equal("smtp.qq.com", options.Host);
        Assert.True(string.IsNullOrEmpty(options.Password));
    }

    [Fact]
    public async Task Swagger_document_lists_scim_endpoints_and_bearer_authentication()
    {
        using var client = factory.CreateClient();

        var uiResponse = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, uiResponse.StatusCode);

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        foreach (var path in new[]
        {
            "/scim/v2/ServiceProviderConfig",
            "/scim/v2/ResourceTypes",
            "/scim/v2/Schemas",
            "/scim/v2/Schemas/{id}",
            "/scim/v2/Users",
            "/scim/v2/Users/{id}",
        })
        {
            Assert.True(paths.TryGetProperty(path, out _), $"Swagger is missing SCIM path {path}.");
        }

        Assert.True(paths.GetProperty("/scim/v2/Users").TryGetProperty("get", out _));
        var createUser = paths.GetProperty("/scim/v2/Users").GetProperty("post");
        Assert.True(createUser.TryGetProperty("requestBody", out var requestBody));
        Assert.True(requestBody.GetProperty("content").TryGetProperty("application/scim+json", out _));
        Assert.Contains(
            createUser.GetProperty("security").EnumerateArray(),
            requirement => requirement.TryGetProperty("Bearer", out _));
        var userOperations = paths.GetProperty("/scim/v2/Users/{id}");
        Assert.True(userOperations.TryGetProperty("get", out _));
        Assert.True(userOperations.TryGetProperty("put", out _));
        Assert.True(userOperations.TryGetProperty("patch", out _));
        Assert.True(userOperations.TryGetProperty("delete", out _));

        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("Bearer", out var bearer));
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task Disabled_scim_removes_all_routes_and_swagger_paths()
    {
        using var disabledFactory = factory.WithWebHostBuilder(builder => builder.UseTestSetting("Scim:Enabled", "false"));
        using var client = disabledFactory.CreateClient();
        var id = Guid.NewGuid();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/scim/v2/ServiceProviderConfig"),
            (HttpMethod.Get, "/scim/v2/ResourceTypes"),
            (HttpMethod.Get, "/scim/v2/Schemas"),
            (HttpMethod.Get, "/scim/v2/Schemas/example"),
            (HttpMethod.Get, "/scim/v2/Users"),
            (HttpMethod.Get, $"/scim/v2/Users/{id}"),
            (HttpMethod.Post, "/scim/v2/Users"),
            (HttpMethod.Put, $"/scim/v2/Users/{id}"),
            (HttpMethod.Patch, $"/scim/v2/Users/{id}"),
            (HttpMethod.Delete, $"/scim/v2/Users/{id}"),
        })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, path));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var swagger = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        using var document = JsonDocument.Parse(await swagger.Content.ReadAsStringAsync());
        Assert.DoesNotContain(document.RootElement.GetProperty("paths").EnumerateObject(),
            path => path.Name.StartsWith("/scim/v2/", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class SwaggerDocumentFactory : WebApplicationFactory<AppWebModule>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development")
            .UseTestSetting("BootstrapAdmin:Username", string.Empty)
            .UseTestSetting("BootstrapAdmin:Password", string.Empty)
            .UseTestSetting("Smtp:Enabled", "false")
            .ConfigureTestServices(services => services.Remove(
                services.Single(descriptor => descriptor.ImplementationType == typeof(BootstrapAdminHostedService))));
    }
}
