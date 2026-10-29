using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using NexusAuth.Workbench.Api.Options;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

[Collection("Workbench environment")]
public sealed class WorkbenchEnvironmentConfigurationTests
{
    [Fact]
    public void Standard_configuration_sources_layer_and_bind_to_workbench_options()
    {
        using var environment = SetEnvironmentVariables(new Dictionary<string, string?>
        {
            ["Auth__ClientId"] = "environment-client",
            ["NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY"] = null,
        });
        var contentRoot = CreateContentRoot(
            """
            {
              "Auth": {
                "Authority": "https://base.example.test",
                "Audience": "base-audience",
                "ClientId": "base-client",
                "ClientSecret": "base-secret",
                "RedirectUri": "https://base.example.test/signin-oidc",
                "PostLogoutRedirectUri": "https://base.example.test/",
                "Scope": "openid profile",
                "RequireHttpsMetadata": true
              },
              "Bootstrap": {
                "ResourceName": "base.resource",
                "ResourceDisplayName": "Base API"
              },
              "Security": {
                "CookieSecurePolicy": "Always",
                "ForwardedHeadersEnabled": false,
                "ForwardLimit": 1
              },
              "Swagger": {
                "Enabled": false
              }
            }
            """,
            """
            {
              "Auth": {
                "Authority": "https://testing.example.test",
                "Audience": "testing-audience",
                "ClientId": "testing-client"
              },
              "Bootstrap": {
                "ResourceName": "testing.resource",
                "ResourceDisplayName": "Testing API"
              },
              "Security": {
                "CookieSecurePolicy": "SameAsRequest",
                "ForwardLimit": 2
              },
              "Swagger": {
                "Enabled": true
              }
            }
            """);

        try
        {
            var builder = CreateBuilder(
                contentRoot,
                "--Auth:Audience=command-line-audience");
            using var configuration = builder.Configuration;

            Assert.Equal("https://testing.example.test", configuration["Auth:Authority"]);
            Assert.Equal("command-line-audience", configuration["Auth:Audience"]);
            Assert.Equal("environment-client", configuration["Auth:ClientId"]);

            var authentication = configuration
                .GetSection(WorkbenchAuthenticationOptions.SectionName)
                .Get<WorkbenchAuthenticationOptions>();
            var bootstrap = configuration
                .GetSection(WorkbenchBootstrapOptions.SectionName)
                .Get<WorkbenchBootstrapOptions>();
            var security = configuration
                .GetSection(WorkbenchSecurityOptions.SectionName)
                .Get<WorkbenchSecurityOptions>();
            var swagger = configuration
                .GetSection(WorkbenchSwaggerOptions.SectionName)
                .Get<WorkbenchSwaggerOptions>();

            Assert.NotNull(authentication);
            Assert.Equal("https://testing.example.test", authentication.Authority);
            Assert.Equal("command-line-audience", authentication.Audience);
            Assert.Equal("environment-client", authentication.ClientId);
            Assert.Equal("base-secret", authentication.ClientSecret);
            Assert.True(authentication.RequireHttpsMetadata);

            Assert.NotNull(bootstrap);
            Assert.Equal("testing.resource", bootstrap.ResourceName);
            Assert.Equal("Testing API", bootstrap.ResourceDisplayName);

            Assert.NotNull(security);
            Assert.Equal("SameAsRequest", security.CookieSecurePolicy);
            Assert.False(security.ForwardedHeadersEnabled);
            Assert.Equal(2, security.ForwardLimit);

            Assert.NotNull(swagger);
            Assert.True(swagger.Enabled);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void Retired_single_underscore_alias_does_not_override_json_configuration()
    {
        using var environment = SetEnvironmentVariables(new Dictionary<string, string?>
        {
            ["Auth__Authority"] = null,
            ["NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY"] = "https://retired-alias.example.test",
        });
        var contentRoot = CreateContentRoot(
            """
            {
              "Auth": {
                "Authority": "https://json.example.test"
              }
            }
            """);

        try
        {
            var builder = CreateBuilder(contentRoot);
            using var configuration = builder.Configuration;
            var authority = configuration["Auth:Authority"];
            var options = configuration
                .GetSection(WorkbenchAuthenticationOptions.SectionName)
                .Get<WorkbenchAuthenticationOptions>();

            Assert.Equal("https://json.example.test", authority);
            Assert.NotNull(options);
            Assert.Equal("https://json.example.test", options.Authority);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    private static WebApplicationBuilder CreateBuilder(string contentRoot, params string[] args) =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
            EnvironmentName = "Testing",
        });

    private static string CreateContentRoot(string appsettings, string? environmentAppsettings = null)
    {
        var contentRoot = Path.Combine(
            Path.GetTempPath(),
            "nexusauth-workbench-configuration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"), appsettings);
        if (environmentAppsettings is not null)
        {
            File.WriteAllText(
                Path.Combine(contentRoot, "appsettings.Testing.json"),
                environmentAppsettings);
        }

        return contentRoot;
    }

    private static IDisposable SetEnvironmentVariables(
        IReadOnlyDictionary<string, string?> values) =>
        new EnvironmentVariableScope(values);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> originalValues;

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            originalValues = values.Keys.ToDictionary(
                name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            foreach (var (name, value) in values)
                Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in originalValues)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
