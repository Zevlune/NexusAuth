using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Extension;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchDependencyInjectionTests
{
    private const string Authority = "https://issuer.example.test";

    [Fact]
    public async Task In_memory_store_is_shared_across_oidc_scopes_and_consumes_atomically()
    {
        var builder = CreateServices()
            .AddNexusAuth(ConfigureValidOptions)
            .UseInMemory();
        using var provider = BuildProvider(builder.Services);
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstOidc = firstScope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>();
        var secondOidc = secondScope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>();

        Assert.NotSame(firstOidc, secondOidc);
        Assert.Same(firstOidc.FlowStateStore, secondOidc.FlowStateStore);

        var flow = new FlowState("code-verifier", "nonce", "browser-binding");
        await firstOidc.FlowStateStore.AddAsync("state", flow);

        Assert.Null(await secondOidc.FlowStateStore.TryTakeAsync("state", "wrong-binding"));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FlowState?> Consume(IFlowStateStore store)
        {
            await start.Task;
            return await store.TryTakeAsync("state", "browser-binding");
        }

        var firstConsumption = Consume(firstOidc.FlowStateStore);
        var secondConsumption = Consume(secondOidc.FlowStateStore);
        start.SetResult();
        var results = await Task.WhenAll(firstConsumption, secondConsumption);

        Assert.Equal(1, results.Count(result => result is not null));
        Assert.Equal(1, results.Count(result => result is null));
        Assert.Same(flow, Assert.Single(results.OfType<FlowState>()));
    }

    [Fact]
    public async Task Discovery_cache_is_shared_across_scopes_and_retries_after_failure()
    {
        var builder = CreateServices()
            .AddNexusAuth(ConfigureValidOptions)
            .UseInMemory();
        using var provider = BuildProvider(builder.Services);
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstCache = firstScope.ServiceProvider.GetRequiredService<IWorkbenchDiscoveryCache>();
        var secondCache = secondScope.ServiceProvider.GetRequiredService<IWorkbenchDiscoveryCache>();

        Assert.Same(firstCache, secondCache);

        var document = new DiscoveryDocument { Issuer = Authority };
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;

        async Task<DiscoveryDocument> Load()
        {
            Interlocked.Increment(ref loadCount);
            loadStarted.TrySetResult();
            await releaseLoad.Task;
            return document;
        }

        var firstLoad = firstCache.GetAsync(Authority, Load, CancellationToken.None);
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondLoad = secondCache.GetAsync(Authority, Load, CancellationToken.None);
        await Task.Yield();
        releaseLoad.SetResult();
        var documents = await Task.WhenAll(firstLoad, secondLoad);

        Assert.Equal(1, Volatile.Read(ref loadCount));
        Assert.Same(document, documents[0]);
        Assert.Same(document, documents[1]);

        const string retryAuthority = "https://retry.example.test";
        var attemptCount = 0;
        async Task<DiscoveryDocument> Fail()
        {
            Interlocked.Increment(ref attemptCount);
            await Task.Yield();
            throw new InvalidOperationException("transient discovery failure");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstCache.GetAsync(retryAuthority, Fail, CancellationToken.None));

        var recovered = new DiscoveryDocument { Issuer = retryAuthority };
        var recoveredDocument = await secondCache.GetAsync(
            retryAuthority,
            () =>
            {
                Interlocked.Increment(ref attemptCount);
                return Task.FromResult(recovered);
            },
            CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref attemptCount));
        Assert.Same(recovered, recoveredDocument);
    }

    [Fact]
    public void Scoped_custom_store_uses_scoped_dependencies_and_is_disposed_with_scope()
    {
        var services = CreateServices();
        services.AddScoped<ScopedStoreDependency>();
        services.AddSingleton<StoreDisposalTracker>();
        var builder = services
            .AddNexusAuth(ConfigureValidOptions)
            .UseInMemory()
            .UseFlowStateStore<ScopedFlowStateStore>(ServiceLifetime.Scoped);

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
            var dependency = scope.ServiceProvider.GetRequiredService<ScopedStoreDependency>();
            Assert.Same(dependency, store.Dependency);
        }

        Assert.Equal(1, provider.GetRequiredService<StoreDisposalTracker>().DisposedCount);
    }

    [Fact]
    public void Store_registration_can_replace_custom_with_memory_and_has_no_concrete_cache_registration()
    {
        var services = CreateServices();
        services.AddScoped<ScopedStoreDependency>();
        services.AddSingleton<StoreDisposalTracker>();
        var builder = services
            .AddNexusAuth(ConfigureValidOptions)
            .UseFlowStateStore<ScopedFlowStateStore>(ServiceLifetime.Scoped)
            .UseInMemory(options => options.Capacity = 17);

        var flowStoreMapping = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IFlowStateStore));
        Assert.Equal(typeof(InMemoryFlowStateStore), flowStoreMapping.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, flowStoreMapping.Lifetime);

        var oidcMapping = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IOidcWorkbenchService));
        Assert.Equal(ServiceLifetime.Scoped, oidcMapping.Lifetime);
        Assert.NotNull(oidcMapping.ImplementationType);
        var cacheParameter = Assert.Single(
            oidcMapping.ImplementationType.GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(IWorkbenchDiscoveryCache));
        Assert.False(cacheParameter.HasDefaultValue);

        var cacheMapping = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IWorkbenchDiscoveryCache));
        Assert.Equal(ServiceLifetime.Singleton, cacheMapping.Lifetime);
        Assert.NotNull(cacheMapping.ImplementationType);
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == cacheMapping.ImplementationType);

        using var provider = BuildProvider(builder.Services);
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<IFlowStateStore>(),
            secondScope.ServiceProvider.GetRequiredService<IFlowStateStore>());
        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<IWorkbenchDiscoveryCache>(),
            secondScope.ServiceProvider.GetRequiredService<IWorkbenchDiscoveryCache>());
    }

    [Fact]
    public void Validate_on_build_reports_missing_flow_store_when_no_store_is_selected()
    {
        var builder = CreateServices().AddNexusAuth(ConfigureValidOptions);

        var exception = Assert.Throws<AggregateException>(
            () => builder.Services.BuildServiceProvider(CreateProviderOptions()));

        Assert.Contains("IFlowStateStore", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_auth_options_fail_startup_validation_with_options_validation_exception()
    {
        var builder = CreateServices()
            .AddNexusAuth(options =>
            {
                options.Authority = Authority;
                options.ClientId = string.Empty;
                options.RedirectUri = string.Empty;
                options.PostLogoutRedirectUri = string.Empty;
                options.Scope = string.Empty;
            })
            .UseInMemory();

        Assert.Contains(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IValidateOptions<WorkbenchAuthOptions>));

        using var provider = BuildProvider(builder.Services);
        var startupValidator = provider.GetRequiredService<IStartupValidator>();

        var exception = Assert.Throws<OptionsValidationException>(() => startupValidator.Validate());

        Assert.Equal(typeof(WorkbenchAuthOptions), exception.OptionsType);
        Assert.Contains(exception.Failures, failure =>
            failure.Contains(nameof(WorkbenchAuthOptions.ClientId), StringComparison.Ordinal));
    }

    [Fact]
    public void In_memory_store_uses_preregistered_time_provider_and_container_disposes_timer()
    {
        var clock = new TrackingTimeProvider();
        var builder = CreateServices();
        builder.AddSingleton<TimeProvider>(clock);
        builder
            .AddNexusAuth(ConfigureValidOptions)
            .UseInMemory();

        Assert.Single(
            builder,
            descriptor => descriptor.ServiceType == typeof(TimeProvider));

        var provider = BuildProvider(builder);
        Assert.IsType<InMemoryFlowStateStore>(
            provider.GetRequiredService<IFlowStateStore>());
        var timer = Assert.Single(clock.Timers);
        Assert.False(timer.Disposed);

        provider.Dispose();

        Assert.True(timer.Disposed);
    }

    private static IServiceCollection CreateServices() => new ServiceCollection();

    private static void ConfigureValidOptions(WorkbenchAuthOptions options)
    {
        options.Authority = Authority;
        options.ClientId = "workbench";
        options.RedirectUri = "https://dashboard.example.test/signin-oidc";
        options.PostLogoutRedirectUri = "https://dashboard.example.test/";
        options.Scope = "openid profile";
    }

    private static ServiceProvider BuildProvider(IServiceCollection services) =>
        services.BuildServiceProvider(CreateProviderOptions());

    private static ServiceProviderOptions CreateProviderOptions() => new()
    {
        ValidateScopes = true,
        ValidateOnBuild = true,
    };

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

        public Task AddAsync(string state, FlowState flowState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<FlowState?> TryTakeAsync(
            string state,
            string browserBinding,
            CancellationToken ct = default) =>
            Task.FromResult<FlowState?>(null);

        public Task<FlowState?> GetAsync(string state, CancellationToken ct = default) =>
            Task.FromResult<FlowState?>(null);

        public Task RemoveAsync(string state, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void Dispose() => disposalTracker.RecordDisposal();
    }

    private sealed class TrackingTimeProvider : TimeProvider
    {
        public List<TrackingTimer> Timers { get; } = [];

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
