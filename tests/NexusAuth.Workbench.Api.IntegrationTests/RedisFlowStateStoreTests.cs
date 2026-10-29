using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Workbench.Api;
using StackExchange.Redis;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class RedisFlowStateStoreTests
{
    internal const string ConnectionVariable = "NEXUSAUTH_TEST_REDIS";

    [RedisFlowFact]
    public async Task Scoped_providers_share_browser_bound_state_across_instances()
    {
        var prefix = NewPrefix();
        using var first = CreateProvider(prefix);
        using var second = CreateProvider(prefix);
        using var login = first.CreateScope();
        using var callback = second.CreateScope();
        var writer = login.ServiceProvider.GetRequiredService<IFlowStateStore>();
        var reader = callback.ServiceProvider.GetRequiredService<IFlowStateStore>();

        Assert.NotSame(writer, reader);
        Assert.Same(
            writer,
            login.ServiceProvider.GetRequiredService<IFlowStateStore>());
        Assert.Null(login.ServiceProvider.GetService<RedisFlowStateStore>());

        await writer.AddAsync("state", new FlowState("binding"));
        Assert.Null(await reader.TryTakeAsync("state", "wrong-browser"));
        Assert.Equal(
            new FlowState("binding"),
            await reader.TryTakeAsync("state", "binding"));
        Assert.Null(await writer.TryTakeAsync("state", "binding"));
    }

    [RedisFlowFact]
    public async Task Concurrent_callbacks_consume_exactly_once()
    {
        var prefix = NewPrefix();
        using var first = CreateProvider(prefix);
        using var second = CreateProvider(prefix);
        using var a = first.CreateScope();
        using var b = second.CreateScope();
        var stores = new[]
        {
            a.ServiceProvider.GetRequiredService<IFlowStateStore>(),
            b.ServiceProvider.GetRequiredService<IFlowStateStore>(),
        };

        await stores[0].AddAsync("race", new FlowState("binding"));
        var results = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(i => stores[i % 2].TryTakeAsync("race", "binding")));

        Assert.Single(results, value => value is not null);
    }

    [RedisFlowFact]
    public async Task Expired_state_releases_capacity_and_cannot_be_consumed()
    {
        using var provider = CreateProvider(
            NewPrefix(),
            capacity: 1,
            lifetime: TimeSpan.FromMilliseconds(100));
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();

        await store.AddAsync("expired", new FlowState("old-binding"));
        await Assert.ThrowsAsync<FlowStateCapacityExceededException>(
            () => store.AddAsync("full", new FlowState("new-binding")));
        await Task.Delay(250);

        Assert.Null(await store.TryTakeAsync("expired", "old-binding"));
        await store.AddAsync("replacement", new FlowState("new-binding"));
        Assert.Equal(
            new FlowState("new-binding"),
            await store.TryTakeAsync("replacement", "new-binding"));
    }

    [RedisFlowFact]
    public async Task Capacity_is_enforced_atomically_across_instances()
    {
        var prefix = NewPrefix();
        using var first = CreateProvider(prefix, capacity: 4);
        using var second = CreateProvider(prefix, capacity: 4);
        using var a = first.CreateScope();
        using var b = second.CreateScope();
        var stores = new[]
        {
            a.ServiceProvider.GetRequiredService<IFlowStateStore>(),
            b.ServiceProvider.GetRequiredService<IFlowStateStore>(),
        };

        var results = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(async i =>
                {
                    try
                    {
                        await stores[i % 2]
                            .AddAsync(i.ToString(), new FlowState("binding"));
                        return true;
                    }
                    catch (FlowStateCapacityExceededException)
                    {
                        return false;
                    }
                }));

        Assert.Equal(4, results.Count(added => added));
        foreach (var i in Enumerable.Range(0, 32))
            await stores[0].RemoveAsync(i.ToString());

        await stores[1].AddAsync("after-cleanup", new FlowState("binding"));
        Assert.Equal(
            new FlowState("binding"),
            await stores[0].TryTakeAsync("after-cleanup", "binding"));
    }

    [RedisFlowFact]
    public async Task Duplicate_state_is_rejected_and_removal_frees_capacity()
    {
        using var provider = CreateProvider(NewPrefix(), capacity: 2);
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();

        await store.AddAsync("same", new FlowState("original-binding"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AddAsync("same", new FlowState("replacement-binding")));

        await store.RemoveAsync("same");
        await store.AddAsync("same", new FlowState("new-binding"));
        Assert.Equal(
            new FlowState("new-binding"),
            await store.TryTakeAsync("same", "new-binding"));
    }

    [RedisFlowFact]
    public async Task Prefixes_isolate_applications_with_identical_state()
    {
        using var first = CreateProvider(NewPrefix());
        using var second = CreateProvider(NewPrefix());
        using var a = first.CreateScope();
        using var b = second.CreateScope();
        var one = a.ServiceProvider.GetRequiredService<IFlowStateStore>();
        var two = b.ServiceProvider.GetRequiredService<IFlowStateStore>();

        await one.AddAsync("same", new FlowState("binding-one"));
        Assert.Null(await two.TryTakeAsync("same", "binding-one"));

        await two.AddAsync("same", new FlowState("binding-two"));
        Assert.Equal(
            new FlowState("binding-one"),
            await one.TryTakeAsync("same", "binding-one"));
        Assert.Equal(
            new FlowState("binding-two"),
            await two.TryTakeAsync("same", "binding-two"));
    }

    [RedisFlowFact]
    public async Task Existing_interface_connection_is_reused_and_not_disposed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(Connection()));
        services.AddWorkbenchFlowState().UseRedis();

        using var provider = services.BuildServiceProvider(Validation());
        var connection = provider.GetRequiredService<IConnectionMultiplexer>();
        using (var scope = provider.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();
            Assert.Same(
                connection,
                scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>());
        }

        Assert.True(connection.IsConnected);
        Assert.Null(provider.GetService<ConnectionMultiplexer>());
    }

    [RedisFlowFact]
    public async Task Thousands_of_expired_flows_are_cleaned_without_lua_overflow()
    {
        var prefix = NewPrefix();
        using var provider = CreateProvider(prefix, capacity: 20_000);
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();
        await store.AddAsync("survivor", new FlowState("binding"));

        var database = provider
            .GetRequiredService<IConnectionMultiplexer>()
            .GetDatabase();
        var recordsKey = (RedisKey)$"{{{prefix}}}:records";
        var expiriesKey = (RedisKey)$"{{{prefix}}}:expiries";

        await database.HashSetAsync(
            recordsKey,
            Enumerable.Range(0, 10_000)
                .Select(i => new HashEntry("expired-" + i, "expired-payload"))
                .ToArray());
        await database.SortedSetAddAsync(
            expiriesKey,
            Enumerable.Range(0, 10_000)
                .Select(i => new SortedSetEntry("expired-" + i, 0))
                .ToArray());

        Assert.Equal(
            new FlowState("binding"),
            await store.TryTakeAsync("survivor", "binding"));
        Assert.Equal(0, await database.HashLengthAsync(recordsKey));
        Assert.Equal(0, await database.SortedSetLengthAsync(expiriesKey));
    }

    [RedisFlowFact]
    public async Task Cancelled_operations_do_not_write_or_consume_state()
    {
        using var provider = CreateProvider(NewPrefix());
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.AddAsync("cancelled", new FlowState("binding"), ct));
        Assert.Null(await store.TryTakeAsync("cancelled", "binding"));

        await store.AddAsync("valid", new FlowState("binding"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.TryTakeAsync("valid", "binding", ct));
        Assert.Equal(
            new FlowState("binding"),
            await store.TryTakeAsync("valid", "binding"));
    }

    private static ServiceProvider CreateProvider(
        string prefix,
        int capacity = 10_000,
        TimeSpan? lifetime = null)
    {
        var services = new ServiceCollection();
        services.Configure<WorkbenchFlowStateOptions>(options =>
        {
            options.Capacity = capacity;
            options.Lifetime = lifetime ?? TimeSpan.FromMinutes(1);
        });
        services
            .AddWorkbenchFlowState()
            .UseRedis(options =>
            {
                options.Configuration = Connection();
                options.KeyPrefix = prefix;
            });
        return services.BuildServiceProvider(Validation());
    }

    private static ServiceProviderOptions Validation() => new()
    {
        ValidateScopes = true,
        ValidateOnBuild = true,
    };

    private static string NewPrefix() =>
        "NexusAuth:di-test:" + Guid.NewGuid().ToString("N");

    private static string Connection() =>
        Environment.GetEnvironmentVariable(ConnectionVariable)!;
}

public sealed class RedisFlowFactAttribute : FactAttribute
{
    public RedisFlowFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    RedisFlowStateStoreTests.ConnectionVariable)))
        {
            Skip =
                $"Set {RedisFlowStateStoreTests.ConnectionVariable} to run isolated Redis flow-state regressions.";
        }
    }
}
