using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchDependencyInjectionTests
{
    [Fact]
    public async Task In_memory_store_is_shared_across_scopes_and_consumes_atomically()
    {
        var services = CreateServices()
            .AddWorkbenchFlowState()
            .UseInMemory()
            .Services;
        using var provider = BuildProvider(services);
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IFlowStateStore>();
        var second = secondScope.ServiceProvider.GetRequiredService<IFlowStateStore>();

        Assert.Same(first, second);

        var flow = new FlowState("browser-binding");
        await first.AddAsync("state", flow);
        Assert.Null(await second.TryTakeAsync("state", "wrong-binding"));

        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FlowState?> Consume(IFlowStateStore store)
        {
            await start.Task;
            return await store.TryTakeAsync("state", "browser-binding");
        }

        var firstConsumption = Consume(first);
        var secondConsumption = Consume(second);
        start.SetResult();
        var results = await Task.WhenAll(firstConsumption, secondConsumption);

        Assert.Equal(1, results.Count(result => result is not null));
        Assert.Equal(1, results.Count(result => result is null));
        Assert.Same(flow, Assert.Single(results.OfType<FlowState>()));
    }

    [Fact]
    public void Custom_store_defaults_to_scoped_and_is_disposed_with_its_scope()
    {
        var services = CreateServices();
        services.AddScoped<ScopedStoreDependency>();
        services.AddSingleton<StoreDisposalTracker>();

        var builder = services
            .AddWorkbenchFlowState()
            .UseFlowStateStore<ScopedFlowStateStore>();

        var mapping = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IFlowStateStore));
        Assert.Equal(typeof(ScopedFlowStateStore), mapping.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, mapping.Lifetime);

        using var provider = BuildProvider(builder.Services);
        using (var scope = provider.CreateScope())
        {
            var store = Assert.IsType<ScopedFlowStateStore>(
                scope.ServiceProvider.GetRequiredService<IFlowStateStore>());
            var dependency =
                scope.ServiceProvider.GetRequiredService<ScopedStoreDependency>();
            Assert.Same(dependency, store.Dependency);
        }

        Assert.Equal(
            1,
            provider.GetRequiredService<StoreDisposalTracker>().DisposedCount);
    }

    [Fact]
    public void In_memory_registration_replaces_custom_store_and_configures_options()
    {
        var services = CreateServices();
        services.AddScoped<ScopedStoreDependency>();
        services.AddSingleton<StoreDisposalTracker>();

        var builder = services
            .AddWorkbenchFlowState()
            .UseFlowStateStore<ScopedFlowStateStore>()
            .UseInMemory(options => options.Capacity = 17);

        var mapping = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IFlowStateStore));
        Assert.Equal(typeof(InMemoryFlowStateStore), mapping.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, mapping.Lifetime);

        using var provider = BuildProvider(builder.Services);
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<IFlowStateStore>(),
            secondScope.ServiceProvider.GetRequiredService<IFlowStateStore>());
        Assert.Equal(
            17,
            provider.GetRequiredService<IOptions<WorkbenchFlowStateOptions>>()
                .Value.Capacity);
    }

    [Fact]
    public async Task In_memory_capacity_is_released_after_flow_expiry()
    {
        var clock = new ManualTimeProvider();
        var services = CreateServices();
        services.AddSingleton<TimeProvider>(clock);
        services
            .AddWorkbenchFlowState()
            .UseInMemory(options =>
            {
                options.Capacity = 1;
                options.Lifetime = TimeSpan.FromMinutes(5);
            });

        using var provider = BuildProvider(services);
        var store = provider.GetRequiredService<IFlowStateStore>();

        await store.AddAsync("expired", new FlowState("old-binding"));
        await Assert.ThrowsAsync<FlowStateCapacityExceededException>(
            () => store.AddAsync("full", new FlowState("new-binding")));

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(await store.TryTakeAsync("expired", "old-binding"));
        await store.AddAsync("replacement", new FlowState("new-binding"));
        Assert.Equal(
            new FlowState("new-binding"),
            await store.TryTakeAsync("replacement", "new-binding"));
    }

    [Fact]
    public void Add_workbench_flow_state_registers_clock_and_options_once()
    {
        var clock = new ManualTimeProvider();
        var services = CreateServices();
        services.AddSingleton<TimeProvider>(clock);

        var builder = services
            .AddWorkbenchFlowState()
            .UseInMemory();

        Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(TimeProvider));

        using var provider = BuildProvider(builder.Services);
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.NotNull(
            provider.GetRequiredService<IOptions<WorkbenchFlowStateOptions>>());
    }

    [Fact]
    public void Container_disposes_the_in_memory_cleanup_timer()
    {
        var clock = new ManualTimeProvider();
        var services = CreateServices();
        services.AddSingleton<TimeProvider>(clock);
        services.AddWorkbenchFlowState().UseInMemory();

        var provider = BuildProvider(services);
        Assert.IsType<InMemoryFlowStateStore>(
            provider.GetRequiredService<IFlowStateStore>());
        var timer = Assert.Single(clock.Timers);
        Assert.False(timer.Disposed);

        provider.Dispose();

        Assert.True(timer.Disposed);
    }

    private static IServiceCollection CreateServices() => new ServiceCollection();

    private static ServiceProvider BuildProvider(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

    private sealed class ScopedStoreDependency
    {
    }

    private sealed class StoreDisposalTracker
    {
        private int disposedCount;

        public int DisposedCount => Volatile.Read(ref disposedCount);

        public void RecordDisposal() => Interlocked.Increment(ref disposedCount);
    }

    private sealed class ScopedFlowStateStore(
        ScopedStoreDependency dependency,
        StoreDisposalTracker disposalTracker) : IFlowStateStore, IDisposable
    {
        public ScopedStoreDependency Dependency { get; } = dependency;

        public Task AddAsync(
            string state,
            FlowState flowState,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<FlowState?> TryTakeAsync(
            string state,
            string browserBinding,
            CancellationToken ct = default) =>
            Task.FromResult<FlowState?>(null);

        public Task RemoveAsync(
            string state,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public void Dispose() => disposalTracker.RecordDisposal();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow =
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public List<TrackingTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new TrackingTimer();
            Timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class TrackingTimer : ITimer
    {
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
