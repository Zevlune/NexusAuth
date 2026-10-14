using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class SwaggerDocumentTests : IClassFixture<SwaggerDocumentFactory>
{
    private readonly SwaggerDocumentFactory factory;

    public SwaggerDocumentTests(SwaggerDocumentFactory factory) => this.factory = factory;

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
}

public sealed class SwaggerDocumentFactory : WebApplicationFactory<AppWebModule>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development")
            .UseSetting("BootstrapAdmin:Username", string.Empty)
            .UseSetting("BootstrapAdmin:Password", string.Empty)
            .UseSetting("Smtp:Enabled", "false")
            .ConfigureTestServices(services => services.Remove(
                services.Single(descriptor => descriptor.ImplementationType == typeof(BootstrapAdminHostedService))));
    }
}
