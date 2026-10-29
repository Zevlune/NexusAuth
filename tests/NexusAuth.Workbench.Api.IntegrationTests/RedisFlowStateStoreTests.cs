using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Extension;
using NexusAuth.Extension.Redis;
using StackExchange.Redis;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class RedisFlowStateStoreTests
{
    internal const string ConnectionVariable = "NEXUSAUTH_TEST_REDIS";

    [RedisFlowFact]
    public async Task Scoped_providers_share_browser_bound_state_across_application_instances()
    {
        var prefix = NewPrefix();
        using var first = CreateProvider(prefix);
        using var second = CreateProvider(prefix);
        using var login = first.CreateScope();
        using var callback = second.CreateScope();
        var writer = login.ServiceProvider.GetRequiredService<IFlowStateStore>();
        var reader = callback.ServiceProvider.GetRequiredService<IFlowStateStore>();
        Assert.NotSame(writer, reader);
        Assert.Same(writer, login.ServiceProvider.GetRequiredService<IFlowStateStore>());
        Assert.Null(login.ServiceProvider.GetService<RedisFlowStateStore>());
        await writer.AddAsync("state", new FlowState("verifier", "nonce", "binding"));
        Assert.Null(await reader.TryTakeAsync("state", "wrong-browser"));
        Assert.Equal("nonce", (await reader.GetAsync("state"))!.Nonce);
        var flow = await reader.TryTakeAsync("state", "binding");
        Assert.Equal("verifier", flow!.CodeVerifier);
        Assert.Equal("nonce", flow.Nonce);
        Assert.Equal("binding", flow.BrowserBinding);
        Assert.Null(await writer.TryTakeAsync("state", "binding"));
    }

    [RedisFlowFact]
    public async Task Concurrent_callbacks_on_different_instances_consume_exactly_once()
    {
        var prefix = NewPrefix();
        using var first = CreateProvider(prefix);
        using var second = CreateProvider(prefix);
        using var a = first.CreateScope();
        using var b = second.CreateScope();
        var stores = new[] { a.ServiceProvider.GetRequiredService<IFlowStateStore>(), b.ServiceProvider.GetRequiredService<IFlowStateStore>() };
        await stores[0].AddAsync("race", new FlowState("v", "n", "binding"));
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => stores[i % 2].TryTakeAsync("race", "binding")));
        Assert.Single(results, value => value is not null);
    }

    [RedisFlowFact]
    public async Task Expired_state_releases_capacity_and_cannot_be_consumed()
    {
        using var provider = CreateProvider(NewPrefix(), capacity: 1, lifetime: TimeSpan.FromMilliseconds(100));
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();
        await store.AddAsync("expired", new FlowState("v", "n", "b"));
        await Assert.ThrowsAsync<FlowStateCapacityExceededException>(() => store.AddAsync("full", new FlowState("v", "n", "b")));
        await Task.Delay(250);
        Assert.Null(await store.TryTakeAsync("expired", "b"));
        Assert.Null(await store.GetAsync("expired"));
        await store.AddAsync("replacement", new FlowState("v", "n", "b"));
        Assert.NotNull(await store.TryTakeAsync("replacement", "b"));
    }

    [RedisFlowFact]
    public async Task Capacity_is_enforced_atomically_across_instances()
    {
        var prefix = NewPrefix();
        using var first = CreateProvider(prefix, capacity: 4);
        using var second = CreateProvider(prefix, capacity: 4);
        using var a = first.CreateScope();
        using var b = second.CreateScope();
        var stores = new[] { a.ServiceProvider.GetRequiredService<IFlowStateStore>(), b.ServiceProvider.GetRequiredService<IFlowStateStore>() };
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
        {
            try { await stores[i % 2].AddAsync(i.ToString(), new FlowState("v", "n", "b")); return true; }
            catch (FlowStateCapacityExceededException) { return false; }
        }));
        Assert.Equal(4, results.Count(added => added));
        foreach (var i in Enumerable.Range(0, 32)) await stores[0].RemoveAsync(i.ToString());
        await stores[1].AddAsync("after-cleanup", new FlowState("v", "n", "b"));
        Assert.NotNull(await stores[0].TryTakeAsync("after-cleanup", "b"));
    }

    [RedisFlowFact]
    public async Task Duplicate_state_does_not_overwrite_the_original_and_removal_frees_capacity()
    {
        using var provider = CreateProvider(NewPrefix(), capacity: 2);
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();
        await store.AddAsync("same", new FlowState("original", "nonce", "binding"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync("same", new FlowState("replacement", "n", "wrong")));
        Assert.Equal("original", (await store.GetAsync("same"))!.CodeVerifier);
        await store.RemoveAsync("same");
        await store.AddAsync("same", new FlowState("new", "n", "b"));
        Assert.Equal("new", (await store.TryTakeAsync("same", "b"))!.CodeVerifier);
    }

    [RedisFlowFact]
    public async Task Prefixes_isolate_applications_even_with_identical_state()
    {
        using var first = CreateProvider(NewPrefix());
        using var second = CreateProvider(NewPrefix());
        using var a = first.CreateScope();
        using var b = second.CreateScope();
        var one = a.ServiceProvider.GetRequiredService<IFlowStateStore>();
        var two = b.ServiceProvider.GetRequiredService<IFlowStateStore>();
        await one.AddAsync("same", new FlowState("one", "n", "b"));
        Assert.Null(await two.GetAsync("same"));
        await two.AddAsync("same", new FlowState("two", "n", "b"));
        Assert.Equal("one", (await one.TryTakeAsync("same", "b"))!.CodeVerifier);
        Assert.Equal("two", (await two.TryTakeAsync("same", "b"))!.CodeVerifier);
    }

    [RedisFlowFact]
    public async Task Existing_interface_connection_is_reused_and_not_disposed_with_request_scope()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(Connection()));
        services.AddNexusAuth(ConfigureAuth).UseRedis();
        using var provider = services.BuildServiceProvider(Validation());
        var connection = provider.GetRequiredService<IConnectionMultiplexer>();
        using (var scope = provider.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();
            Assert.Same(connection, scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>());
        }
        Assert.True(connection.IsConnected);
        Assert.Null(provider.GetService<ConnectionMultiplexer>());
    }

    [RedisFlowFact]
    public async Task Thousands_of_expired_flows_are_cleaned_without_lua_argument_overflow()
    {
        var prefix = NewPrefix();
        using var provider = CreateProvider(prefix, capacity: 20_000);
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();
        await store.AddAsync("survivor", new FlowState("v", "n", "b"));
        var database = provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var recordsKey = (RedisKey)$"{{{prefix}}}:records";
        var expiriesKey = (RedisKey)$"{{{prefix}}}:expiries";
        // Keep one live flow so the collection TTL cannot hide a large batch of expired members.
        await database.HashSetAsync(recordsKey, Enumerable.Range(0, 10_000)
            .Select(i => new HashEntry("expired-" + i, "expired-payload")).ToArray());
        await database.SortedSetAddAsync(expiriesKey, Enumerable.Range(0, 10_000)
            .Select(i => new SortedSetEntry("expired-" + i, 0)).ToArray());
        Assert.NotNull(await store.GetAsync("survivor"));
        Assert.Equal(1, await database.HashLengthAsync(recordsKey));
        Assert.Equal(1, await database.SortedSetLengthAsync(expiriesKey));
        Assert.NotNull(await store.TryTakeAsync("survivor", "b"));
    }

    [RedisFlowFact]
    public async Task Cancelled_operations_do_not_write_or_consume_state()
    {
        using var provider = CreateProvider(NewPrefix());
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFlowStateStore>();
        var ct = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddAsync("cancelled", new FlowState("v", "n", "b"), ct));
        Assert.Null(await store.GetAsync("cancelled"));
        await store.AddAsync("valid", new FlowState("v", "n", "b"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryTakeAsync("valid", "b", ct));
        Assert.NotNull(await store.TryTakeAsync("valid", "b"));
    }

    private static ServiceProvider CreateProvider(string prefix, int capacity = 10_000, TimeSpan? lifetime = null)
    {
        var services = new ServiceCollection();
        services.Configure<WorkbenchFlowStateOptions>(options =>
        {
            options.Capacity = capacity;
            options.Lifetime = lifetime ?? TimeSpan.FromMinutes(1);
        });
        services.AddNexusAuth(ConfigureAuth).UseRedis(options =>
        {
            options.Configuration = Connection();
            options.KeyPrefix = prefix;
        });
        return services.BuildServiceProvider(Validation());
    }

    private static void ConfigureAuth(WorkbenchAuthOptions options)
    {
        options.Authority = "https://issuer.example.test";
        options.ClientId = "workbench";
        options.RedirectUri = "https://app.example.test/signin-oidc";
        options.PostLogoutRedirectUri = "https://app.example.test/";
        options.Scope = "openid";
    }

    private static ServiceProviderOptions Validation() => new() { ValidateScopes = true, ValidateOnBuild = true };
    private static string NewPrefix() => "NexusAuth:di-test:" + Guid.NewGuid().ToString("N");
    private static string Connection() => Environment.GetEnvironmentVariable(ConnectionVariable)!;
}

public sealed class RedisFlowFactAttribute : FactAttribute
{
    public RedisFlowFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RedisFlowStateStoreTests.ConnectionVariable)))
            Skip = $"Set {RedisFlowStateStoreTests.ConnectionVariable} to run isolated Redis flow-state regressions.";
    }
}
