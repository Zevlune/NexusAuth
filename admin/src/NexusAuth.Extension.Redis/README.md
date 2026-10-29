# NexusAuth Redis Flow-State Provider

This optional package stores pending Workbench OIDC flows in Redis so login initiation and callback
requests can reach different application instances.

`NexusAuth.Extension` is a standalone integration SDK and does not depend on
`NexusAuth.Shared` or any server-side project. The core SDK uses standard .NET and OIDC
packages. Add this optional Redis provider separately when applications need shared flow state.

## Registration

Use an application-owned multiplexer:

```csharp
services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(configuration));

services.AddNexusAuth(options => { /* OIDC options */ })
    .UseRedis();
```

Or let the provider create and own the multiplexer:

```csharp
services.AddNexusAuth(options => { /* OIDC options */ })
    .UseRedis(redis =>
    {
        redis.Configuration = configuration;
        redis.KeyPrefix = "NexusAuth:workbench:flow";
        redis.Database = -1;
    });
```

The provider-created `IConnectionMultiplexer` is registered as a DI-owned singleton and disposed with
the container. The parameterless overload requires an existing registration and never disposes that
connection. No concrete multiplexer type is registered.

## Shared Capacity

`WorkbenchFlowStateOptions.Capacity` is a shared Redis limit. Every instance using the same Redis
database and key prefix competes for the same slots. It is not a per-process or per-request limit.
`Lifetime` may be up to one hour, and `Capacity` may be up to 100,000 pending flows.

Keep the lifetime and capacity configuration consistent across instances. The default capacity is
10,000 and the default lifetime is ten minutes.

## Key Isolation

The default `KeyPrefix` is `NexusAuth:workbench:flow`. The store creates these two keys:

```text
{NexusAuth:workbench:flow}:records
{NexusAuth:workbench:flow}:expiries
```

The fixed hash tag in braces keeps both keys in one Redis Cluster slot, allowing one Lua script to
update the record hash and expiry sorted set atomically. A custom prefix must not contain braces.

Use a distinct prefix for each application or tenant that must have an isolated flow-state pool.
Changing the prefix changes the Redis Cluster slot and the capacity pool. Different prefixes do not
share state, capacity, or cleanup.

## Stored Data

Each hash value is JSON containing a payload with the PKCE verifier, nonce, and original
`BrowserBinding`, plus a separate SHA-256 browser-binding hash. The Lua comparison uses only the
hash, so raw binding values are never compared. Both keys receive a `PEXPIRE` based on the latest
record expiry. Redis `TIME` is authoritative for expiration checks, so application clock skew does
not affect capacity or consumption.

`TryTakeAsync` atomically compares the binding hash and removes both the record and expiry index only
on a match. A wrong binding leaves the flow available for a later valid callback. A concurrent
successful callback consumes the flow exactly once.
