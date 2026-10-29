using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusAuth.Extension;
using StackExchange.Redis;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class OpenIdConnectFlowStateStorageConfigurationTests
{
    [Fact]
    public async Task Configuration_defaults_to_a_singleton_in_memory_store()
    {
        var clock = new ManualTimeProvider();
        using var host = CreateBuilder(
                new Dictionary<string, string?>(),
                services => services.AddSingleton<TimeProvider>(clock))
            .Build();
        await host.StartAsync();

        IFlowStateStore writer;
        using (var scope = host.Services.CreateScope())
        {
            writer = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();
            await writer.AddAsync("configured", new FlowState("binding"));
        }

        Assert.False(clock.Timer.Disposed);

        using var callbackScope = host.Services.CreateScope();
        var reader =
            callbackScope.ServiceProvider.GetRequiredService<IFlowStateStore>();

        Assert.NotSame(writer, reader);
        Assert.Equal(
            new FlowState("binding"),
            await reader.TryTakeAsync("configured", "binding"));
        Assert.False(clock.Timer.Disposed);
    }

    [Theory]
    [InlineData("Auth:FlowState:Provider", "inmemory")]
    [InlineData("Auth:FlowState:Capacity", "0")]
    [InlineData("Auth:FlowState:Lifetime", "02:00:00")]
    public async Task Invalid_flow_options_fail_at_startup(string key, string value)
    {
        using var host = CreateBuilder(
                new Dictionary<string, string?> { [key] = value })
            .Build();

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());

        Assert.Contains("Auth:FlowState", exception.ToString());
    }

    [Fact]
    public async Task Missing_redis_configuration_fails_at_startup()
    {
        using var host = CreateBuilder(
                new Dictionary<string, string?>
                {
                    ["Auth:FlowState:Provider"] = "Redis",
                })
            .Build();

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());

        Assert.Contains(
            "Auth:FlowState:Redis Configuration is required",
            exception.ToString());
    }

    [Theory]
    [InlineData("Auth:FlowState:Redis:KeyPrefix", "{invalid}")]
    [InlineData("Auth:FlowState:Redis:Database", "-2")]
    [InlineData(
        "Auth:FlowState:Redis:Configuration",
        "localhost:6379,abortConnect=not-a-bool")]
    public async Task Invalid_redis_options_fail_at_startup(string key, string value)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:FlowState:Provider"] = "Redis",
            ["Auth:FlowState:Redis:Configuration"] = "localhost:6379",
        };
        settings[key] = value;
        using var host = CreateBuilder(settings).Build();

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());

        Assert.Contains("Auth:FlowState:Redis", exception.ToString());
    }

    [Fact]
    public async Task Configured_redis_connection_is_not_created_during_startup()
    {
        using var host = CreateBuilder(
                new Dictionary<string, string?>
                {
                    ["Auth:FlowState:Provider"] = "Redis",
                    ["Auth:FlowState:Redis:Configuration"] =
                        "localhost:1,connectTimeout=100,connectRetry=0,abortConnect=true",
                })
            .Build();

        await host.StartAsync();
    }

    [Fact]
    public async Task Registered_redis_connection_is_reused_without_startup_connection()
    {
        var connectionResolutions = 0;
        using var host = CreateBuilder(
                new Dictionary<string, string?>
                {
                    ["Auth:FlowState:Provider"] = "Redis",
                },
                services => services.AddSingleton<IConnectionMultiplexer>(_ =>
                {
                    Interlocked.Increment(ref connectionResolutions);
                    throw new InvalidOperationException(
                        "The Redis connection should remain lazy.");
                }))
            .Build();

        await host.StartAsync();

        Assert.Equal(0, Volatile.Read(ref connectionResolutions));
    }

    [Fact]
    public async Task Custom_store_replaces_the_configuration_forwarder()
    {
        var builder = CreateBuilder(
            new Dictionary<string, string?>(),
            services => services.AddScoped<CustomFlowStateStore>(),
            configureFlowState: false);
        builder.Services
            .AddOpenIdConnectFlowState()
            .UseConfiguration(builder.Configuration.GetSection("Auth:FlowState"))
            .UseFlowStateStore<CustomFlowStateStore>();

        var descriptor = Assert.Single(
            builder.Services,
            item => !item.IsKeyedService
                && item.ServiceType == typeof(IFlowStateStore));
        Assert.Equal(typeof(CustomFlowStateStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        using var host = builder.Build();
        await host.StartAsync();
        using var scope = host.Services.CreateScope();
        var store =
            Assert.IsType<CustomFlowStateStore>(
                scope.ServiceProvider.GetRequiredService<IFlowStateStore>());

        await store.AddAsync("custom", new FlowState("binding"));
        Assert.Equal(
            new FlowState("binding"),
            await store.TryTakeAsync("custom", "binding"));
    }

    private static HostApplicationBuilder CreateBuilder(
        IReadOnlyDictionary<string, string?> settings,
        Action<IServiceCollection>? configureServices = null,
        bool configureFlowState = true)
    {
        var builder = new HostApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        configureServices?.Invoke(builder.Services);
        if (configureFlowState)
        {
            builder.Services
                .AddOpenIdConnectFlowState()
                .UseConfiguration(
                    builder.Configuration.GetSection("Auth:FlowState"));
        }

        return builder;
    }

    private sealed class CustomFlowStateStore : IFlowStateStore
    {
        private readonly Dictionary<string, FlowState> flows =
            new(StringComparer.Ordinal);

        public Task AddAsync(
            string state,
            FlowState flowState,
            CancellationToken ct = default)
        {
            flows.Add(state, flowState);
            return Task.CompletedTask;
        }

        public Task<FlowState?> TryTakeAsync(
            string state,
            string browserBinding,
            CancellationToken ct = default)
        {
            if (!flows.Remove(state, out var flow)
                || flow.BrowserBinding != browserBinding)
            {
                return Task.FromResult<FlowState?>(null);
            }

            return Task.FromResult<FlowState?>(flow);
        }

        public Task RemoveAsync(string state, CancellationToken ct = default)
        {
            flows.Remove(state);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public TrackingTimer Timer { get; } = new();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) =>
            Timer;
    }

    private sealed class TrackingTimer : ITimer
    {
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
