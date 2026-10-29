using System.Reflection;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.OpenApi;
using NexusAuth.Application.Services.Scim;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class CredentialLifecycleTests
{
    [Fact]
    public async Task Revoked_scim_credential_cannot_be_reactivated_but_disable_is_reversible()
    {
        var (service, store) = CreateScimService();
        var created = await service.CreateAsync("idp", null, null);
        var id = created.Credential.Id;

        var disabled = await service.UpdateAsync(id, "idp", null, null, isActive: false);
        Assert.False(disabled!.IsActive);
        Assert.Null(disabled.RevokedAt);
        Assert.Equal(ScimCredentialValidationResult.InvalidToken, await service.AuthorizeAsync(created.Token, "scim:read"));
        var enabled = await service.UpdateAsync(id, "idp", null, null, isActive: true);
        Assert.True(enabled!.IsActive);
        Assert.Equal(ScimCredentialValidationResult.Granted, await service.AuthorizeAsync(created.Token, "scim:read"));

        Assert.True(await service.RevokeAsync(id));
        var revokedAt = store.Single().RevokedAt;
        Assert.NotNull(revokedAt);
        await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() => service.UpdateAsync(id, "idp", null, null, isActive: true));
        var renamed = await service.UpdateAsync(id, "idp-renamed", null, null, isActive: false);
        Assert.False(renamed!.IsActive);
        Assert.Equal(revokedAt, renamed.RevokedAt);
        Assert.Equal(ScimCredentialValidationResult.InvalidToken, await service.AuthorizeAsync(created.Token, "scim:read"));
    }

    [Fact]
    public async Task Scim_credential_scopes_are_allow_listed_and_null_keeps_the_current_scopes()
    {
        var (service, _) = CreateScimService();
        var created = await service.CreateAsync("reader", ["scim:read"], null);
        var id = created.Credential.Id;

        var updated = await service.UpdateAsync(id, "reader", null, null, isActive: true);
        Assert.Equal(["scim:read"], updated!.Scopes);
        Assert.Equal(ScimCredentialValidationResult.InsufficientScope, await service.AuthorizeAsync(created.Token, "scim:write"));
        Assert.Equal(ScimCredentialValidationResult.InvalidToken, await service.AuthorizeAsync("unknown-token", "scim:read"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(id, "reader", [], null, isActive: true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(id, "reader", ["scim:admin"], null, isActive: true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("bad", ["scim:read", "users:delete"], null));
        Assert.Equal(["scim:read", "scim:write"], (await service.CreateAsync("default", null, null)).Credential.Scopes);
    }

    [Fact]
    public async Task Expired_scim_credential_can_be_edited_when_the_expiry_is_unchanged()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1);
        var credential = ScimServicePrincipalCredential.CreateWithToken("expired", "expired-token", ["scim:read"], past);
        var (service, store) = CreateScimService(credential);

        // The dashboard round-trips the stored value, possibly rounded to milliseconds.
        var roundTripped = new DateTimeOffset(past.UtcTicks - past.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var updated = await service.UpdateAsync(credential.Id, "expired-renamed", ["scim:read"], roundTripped, isActive: true);

        Assert.Equal("expired-renamed", updated!.Name);
        Assert.Equal(roundTripped, store.Single().ExpiresAt);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(credential.Id, "expired-renamed", ["scim:read"], past.AddDays(-1), isActive: true));
    }

    [Fact]
    public async Task Revoked_open_api_credential_cannot_be_reactivated_but_disable_is_reversible()
    {
        var (service, store) = CreateOpenApiService();
        var created = await service.CreateAsync("app-reader", OpenApiCredential.TargetTypeApplication, null);
        var id = created.Credential.Id;

        var disabled = await service.UpdateAsync(id, "app-reader", null, isActive: false);
        Assert.False(disabled!.IsActive);
        Assert.Null(disabled.RevokedAt);
        Assert.False(await service.ValidateAsync(created.Token, OpenApiCredential.TargetTypeApplication));
        Assert.True((await service.UpdateAsync(id, "app-reader", null, isActive: true))!.IsActive);
        Assert.True(await service.ValidateAsync(created.Token, OpenApiCredential.TargetTypeApplication));

        Assert.True(await service.RevokeAsync(id));
        var revokedAt = store.Single().RevokedAt;
        Assert.NotNull(revokedAt);
        await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() => service.UpdateAsync(id, "app-reader", null, isActive: true));
        var renamed = await service.UpdateAsync(id, "app-reader-old", null, isActive: false);
        Assert.Equal(revokedAt, renamed!.RevokedAt);
        Assert.True(await service.RevokeAsync(id));
        Assert.Equal(revokedAt, store.Single().RevokedAt);
        Assert.False(await service.ValidateAsync(created.Token, OpenApiCredential.TargetTypeApplication));
    }

    [Fact]
    public async Task Expired_open_api_credential_can_be_edited_when_the_expiry_is_unchanged()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-2);
        var credential = OpenApiCredential.CreateWithToken("expired", "expired-token", OpenApiCredential.TargetTypeServiceResource, past);
        var (service, _) = CreateOpenApiService(credential);

        var updated = await service.UpdateAsync(credential.Id, "expired-renamed", past, isActive: true);

        Assert.Equal("expired-renamed", updated!.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(credential.Id, "expired-renamed", past.AddHours(-1), isActive: true));
    }

    private static (ScimCredentialService Service, List<ScimServicePrincipalCredential> Store) CreateScimService(params ScimServicePrincipalCredential[] seed)
    {
        var store = seed.ToList();
        var repository = InMemoryCredentialRepository.Create<IScimServicePrincipalCredentialRepository, ScimServicePrincipalCredential>(
            store, item => item.Id, item => item.Name, item => item.TokenHash);
        return (new ScimCredentialService(repository), store);
    }

    private static (OpenApiCredentialService Service, List<OpenApiCredential> Store) CreateOpenApiService(params OpenApiCredential[] seed)
    {
        var store = seed.ToList();
        var repository = InMemoryCredentialRepository.Create<IOpenApiCredentialRepository, OpenApiCredential>(
            store, item => item.Id, item => item.Name, item => item.TokenHash);
        return (new OpenApiCredentialService(repository), store);
    }
}

/// <summary>Minimal list-backed implementation of the credential repository members the services use.</summary>
public class InMemoryCredentialRepository : DispatchProxy
{
    private Func<string, object?[], object?> handler = (name, _) => throw new NotSupportedException(name);

    public static TRepository Create<TRepository, TEntity>(List<TEntity> store, Func<TEntity, Guid> id, Func<TEntity, string> name,
        Func<TEntity, string> tokenHash) where TRepository : class
    {
        var proxy = DispatchProxy.Create<TRepository, InMemoryCredentialRepository>();
        ((InMemoryCredentialRepository)(object)proxy).handler = (method, args) => method switch
        {
            "FindByIdAsync" => Task.FromResult(store.FirstOrDefault(item => id(item) == (Guid)args[0]!)),
            "FindByNameAsync" => Task.FromResult(store.FirstOrDefault(item => name(item) == (string)args[0]!)),
            "FindByTokenHashAsync" => Task.FromResult(store.FirstOrDefault(item => tokenHash(item) == (string)args[0]!)),
            "GetAllAsync" => Task.FromResult<IReadOnlyList<TEntity>>(store.ToArray()),
            "AddAsync" => AddAsync(store, (TEntity)args[0]!),
            "UpdateAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(method),
        };
        return proxy;
    }

    private static Task AddAsync<TEntity>(List<TEntity> store, TEntity entity)
    {
        store.Add(entity);
        return Task.CompletedTask;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        handler(targetMethod?.Name ?? string.Empty, args ?? []);
}
