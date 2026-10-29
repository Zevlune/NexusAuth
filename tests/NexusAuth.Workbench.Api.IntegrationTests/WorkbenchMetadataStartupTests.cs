using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Extension;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchMetadataStartupTests
{
    [Fact]
    public async Task Startup_primes_the_same_metadata_manager_used_by_the_standard_handler_and_scoped_client()
    {
        var metadata = new TrackingMetadata();
        await using var factory = CreateFactory(metadata);
        var manager = factory.Services.GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>();
        await metadata.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme);
        Assert.Same(manager, options.ConfigurationManager);
        Assert.True(options.UsePkce);
        Assert.True(options.SaveTokens);
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var firstClient = first.ServiceProvider.GetRequiredService<INexusAuthClient>();
        var secondClient = second.ServiceProvider.GetRequiredService<INexusAuthClient>();
        Assert.NotSame(firstClient, secondClient);
        Assert.Same(metadata.Configuration, await firstClient.FetchConfigurationAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unavailable_or_unresponsive_provider_does_not_block_startup_or_shutdown(bool throwImmediately)
    {
        var metadata = new TrackingMetadata { ThrowImmediately = throwImmediately, NeverComplete = !throwImmediately };
        var factory = CreateFactory(metadata);
        try
        {
            Assert.NotNull(factory.Services);
            await metadata.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("Auth:FlowState:Provider", "Unknown")]
    [InlineData("Auth:FlowState:Capacity", "0")]
    [InlineData("Auth:FlowState:Lifetime", "02:00:00")]
    public void Invalid_flow_storage_configuration_is_rejected_at_startup(string key, string value)
    {
        using var factory = CreateFactory(new TrackingMetadata()).WithWebHostBuilder(builder => builder.UseSetting(key, value));
        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);
        Assert.Contains("Auth:FlowState", exception.ToString());
    }

    private static WebApplicationFactory<WorkbenchApiModule> CreateFactory(TrackingMetadata metadata) =>
        new WebApplicationFactory<WorkbenchApiModule>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing")
                .UseSetting("Auth:Authority", "https://issuer.example.test")
                .UseSetting("Auth:ClientId", "workbench")
                .UseSetting("Auth:ClientSecret", "test-only-secret")
                .UseSetting("Auth:RedirectUri", "https://dashboard.example.test/signin-oidc")
                .UseSetting("Auth:PostLogoutRedirectUri", "https://dashboard.example.test/");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConfigurationManager<OpenIdConnectConfiguration>>();
                services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(metadata);
            });
        });

    private sealed class TrackingMetadata : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public OpenIdConnectConfiguration Configuration { get; } = new() { Issuer = "https://issuer.example.test" };
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowImmediately { get; init; }
        public bool NeverComplete { get; init; }
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            Called.TrySetResult();
            if (ThrowImmediately) throw new HttpRequestException("Provider unavailable.");
            if (NeverComplete) return new TaskCompletionSource<OpenIdConnectConfiguration>().Task;
            return Task.FromResult(Configuration);
        }
        public void RequestRefresh() { }
    }
}
