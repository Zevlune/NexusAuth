using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchAuthenticationOptionsTests
{
    [Fact]
    public void Bound_options_normalize_credentials_and_configure_the_native_handler_without_registering_the_options_object()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = " https://issuer.test/ ",
            ["Auth:ClientId"] = " workbench-client ",
            ["Auth:ClientSecret"] = " test-secret ",
            ["Auth:Scope"] = "openid profile offline_access workbench.api workbench.api",
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkbenchAuthentication(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.Null(provider.GetService<WorkbenchAuthenticationOptions>());
        var authentication = provider.GetRequiredService<IOptions<WorkbenchAuthenticationOptions>>().Value;
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme);
        Assert.Equal("https://issuer.test", authentication.Authority);
        Assert.Equal("workbench-client", authentication.ClientId);
        Assert.Equal("test-secret", authentication.ClientSecret);
        Assert.Equal("openid profile offline_access workbench.api", authentication.Scope);
        Assert.Equal(authentication.Authority, oidc.Authority);
        Assert.Equal(authentication.ClientId, oidc.ClientId);
        Assert.Equal(authentication.ClientSecret, oidc.ClientSecret);
        Assert.Equal(authentication.Scope, string.Join(' ', oidc.Scope));
        Assert.NotNull(oidc.ConfigurationManager);
    }

    [Theory]
    [InlineData("Auth:ClientId", "", "Auth:ClientId")]
    [InlineData("Auth:Scope", "", "Auth:Scope")]
    [InlineData("Auth:Authority", "https://user@issuer.test", "Auth:Authority")]
    [InlineData("Auth:Authority", "http://issuer.test", "Auth:Authority")]
    public void Invalid_authentication_configuration_fails_the_options_startup_validation(
        string key, string value, string expectedError)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkbenchAuthentication(Configuration(new Dictionary<string, string?> { [key] = value }));
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(expectedError, exception.Message);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["Auth:Authority"] = "https://issuer.test",
            ["Auth:Audience"] = "workbench.api",
            ["Auth:ClientId"] = "workbench-client",
            ["Auth:ClientSecret"] = "test-secret",
            ["Auth:RedirectUri"] = "https://dashboard.test/signin-oidc",
            ["Auth:PostLogoutRedirectUri"] = "https://dashboard.test/",
            ["Auth:Scope"] = "openid profile offline_access",
        };
        foreach (var (key, value) in overrides) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
