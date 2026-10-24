using Microsoft.Extensions.Configuration;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

[Collection("Workbench environment")]
public sealed class WorkbenchEnvironmentConfigurationTests
{
    [Fact]
    public void Authority_environment_variable_maps_to_auth_options()
    {
        const string variable = "NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY";
        var originalValue = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "https://provider.example.test");
            using var configuration = new ConfigurationManager();

            configuration.AddWorkbenchEnvironmentVariables();

            Assert.Equal("https://provider.example.test", configuration["Auth:Authority"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, originalValue);
        }
    }

    [Fact]
    public void Retired_backchannel_environment_variable_is_not_mapped()
    {
        const string variable = "NEXUSAUTH_WORKBENCH_AUTH_BACKCHANNEL_AUTHORITY";
        var originalValue = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "http://retired-provider:8080");
            using var configuration = new ConfigurationManager();
            configuration.AddWorkbenchEnvironmentVariables();
            Assert.Null(configuration["Auth:BackchannelAuthority"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, originalValue);
        }
    }

    [Fact]
    public void Independent_bootstrap_variables_bind_to_the_workbench_options()
    {
        var values = new Dictionary<string, string>
        {
            ["NEXUSAUTH_WORKBENCH_GATEWAY_ENABLED"] = "true",
            ["NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_NAME"] = "nexusauth.workbench.api",
            ["NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_DISPLAY_NAME"] = "Workbench API",
            ["NEXUSAUTH_WORKBENCH_BOOTSTRAP_CLIENT_NAME"] = "Workbench",
            ["NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_DESCRIPTION"] = "Service description",
            ["NEXUSAUTH_WORKBENCH_BOOTSTRAP_CLIENT_DESCRIPTION"] = "Application description",
        };
        var originalValues = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in values)
                Environment.SetEnvironmentVariable(name, value);
            using var configuration = new ConfigurationManager();
            configuration.AddWorkbenchEnvironmentVariables();
            Assert.True(configuration.IsWorkbenchGatewayEnabled());

            var options = configuration.GetSection("Bootstrap").Get<WorkbenchBootstrapOptions>();

            Assert.Equal("nexusauth.workbench.api", options!.ResourceName);
            Assert.Equal("Workbench API", options.ResourceDisplayName);
            Assert.Equal("Workbench", options.ClientName);
            Assert.Equal("Service description", options.ResourceDescription);
            Assert.Equal("Application description", options.ClientDescription);
        }
        finally
        {
            foreach (var (name, value) in originalValues)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
