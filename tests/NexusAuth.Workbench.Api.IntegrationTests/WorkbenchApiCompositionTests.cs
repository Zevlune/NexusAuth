using NexusAuth.Workbench.Api.Bootstrap;
using NexusAuth.Workbench.Api.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Users;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchApiCompositionTests : IClassFixture<WebApplicationFactory<WorkbenchApiModule>>
{
    private readonly WebApplicationFactory<WorkbenchApiModule> factory;

    public WorkbenchApiCompositionTests(WebApplicationFactory<WorkbenchApiModule> factory)
    {
        this.factory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Testing")
            .UseSetting("Auth:Authority", "https://issuer.example.test")
            .UseSetting("Auth:ClientSecret", "test-only-client-secret")
            .UseSetting("Swagger:Enabled", "true"));
    }

    [Fact]
    public void Host_registers_only_the_workbench_resource_initializer()
    {
        var hostedServices = factory.Services.GetServices<IHostedService>().ToArray();

        Assert.Single(hostedServices.OfType<WorkbenchClientCredentialHostedService>());
        Assert.DoesNotContain(hostedServices, service => service.GetType().Name == "GatewayClientCredentialHostedService");
    }

    [Fact]
    public async Task Swagger_endpoint_is_available_when_the_real_workbench_host_starts()
    {
        using var configured = new WorkbenchAdministrationAuthorizationFixture();
        using var anonymous = configured.CreateAnonymousClient();
        using var denied = await anonymous.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Equal("/api/auth/login", denied.Headers.Location?.AbsolutePath);

        using var administrator = configured.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        using var response = await administrator.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Swagger_is_not_exposed_by_default_in_production()
    {
        using var production = new WebApplicationFactory<WorkbenchApiModule>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production")
                    .UseSetting("Auth:Authority", "https://issuer.example.test")
                    .UseSetting("Auth:ClientSecret", "test-only-client-secret");
                builder.ConfigureServices(services =>
                {
                    var bootstrap = services.SingleOrDefault(descriptor =>
                        descriptor.ImplementationType == typeof(WorkbenchClientCredentialHostedService));
                    if (bootstrap is not null)
                        services.Remove(bootstrap);
                    services.PostConfigure<AuthorizationOptions>(
                        options => options.FallbackPolicy = null);
                });
            });
        Assert.Equal("Production", production.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        Assert.Null(production.Services.GetRequiredService<IOptions<WorkbenchSwaggerOptions>>().Value.Enabled);
        using var client = production.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var document = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, document.StatusCode);
        using var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ui.StatusCode);
    }

    [Fact]
    public async Task User_management_endpoint_requires_authentication()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/users");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void User_management_service_resolves_without_the_provider_token_signing_services()
    {
        using var scope = factory.Services.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IUserManagementService>();

        Assert.NotNull(service);
    }

    [Fact]
    public void Passkey_management_resolves_without_provider_signing_services()
    {
        using var scope = factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPasskeyManagementService>());
    }

    [Fact]
    public async Task Passkey_details_require_authentication()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync($"/api/users/{Guid.NewGuid()}/passkeys");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
