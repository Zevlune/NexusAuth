using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.ApiResources;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class ResourceLifecycleTests
{
    [Fact]
    public async Task Register_rejects_a_duplicate_audience()
    {
        var existing = ApiResource.Create("orders", "Orders", "orders.api");
        using var harness = new ApiResourceServiceHarness(existing);

        await Assert.ThrowsAsync<ApiResourceAlreadyExistsException>(() => harness.Service.RegisterAsync(
            "billing",
            "Billing",
            "orders.api"));

        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task Update_rejects_changing_an_existing_audience()
    {
        var resource = ApiResource.Create("orders", "Orders", "orders.api");
        using var harness = new ApiResourceServiceHarness(resource);

        await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() => harness.Service.UpdateAsync(
            resource.Id,
            new UpdateApiResourceRequest("Orders", "billing.api", null, true)));

        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task Client_management_rejects_resource_scopes_not_selected_by_id()
    {
        using var harness = new ClientServiceHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.CreateAsync(new CreateClientRequest(
            "client",
            "Client",
            null,
            null,
            null,
            ["unselected.api"],
            [OAuthGrantTypes.ClientCredentials],
            false,
            OAuthClientAuthenticationMethods.ClientSecretPost,
            false,
            null,
            null,
            [])));

        Assert.DoesNotContain("client.add", harness.Calls);
    }

    [Fact]
    public async Task ValidateScopes_rejects_a_resource_not_assigned_to_the_client()
    {
        var client = OAuthClient.Create(
            "client",
            "Client",
            allowedScopes: ["orders.api"]);
        var resource = ApiResource.Create("orders", "Orders", "orders.api");
        using var harness = new ClientServiceHarness(client, resource);

        var result = await harness.Service.ValidateScopesAsync("client", "orders.api", allowIdentityScopes: false);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_scope", result.ErrorCode);
        Assert.Contains("not assigned", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_management_cannot_assign_a_managed_resource(bool updating)
    {
        var client = OAuthClient.Create("client", "Client");
        var resource = ApiResource.Create("workbench", "Workbench", "workbench.api");
        resource.MarkAsSystemManaged();
        using var harness = new ClientServiceHarness(client, resource);

        await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(async () =>
        {
            if (updating)
                await harness.Service.UpdateAsync(client.Id, new UpdateClientRequest(
                    null, null, null, null, null, null, null, null, null, null, null,
                    ApiResourceIds: [resource.Id]));
            else
                await harness.Service.CreateAsync(new CreateClientRequest(
                    "new-client", "New client", null, null, null, [],
                    [OAuthGrantTypes.ClientCredentials], false,
                    OAuthClientAuthenticationMethods.ClientSecretPost,
                    false, null, null, [resource.Id]));
        });
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task Assign_and_revoke_validate_ids_and_are_idempotent()
    {
        var client = OAuthClient.Create("client", "Client");
        var resource = ApiResource.Create("orders", "Orders", "orders.api");
        using var harness = new ApiResourceServiceHarness(resource, client);

        await harness.Service.AssignToClientAsync(client.Id, resource.Id);
        await harness.Service.AssignToClientAsync(client.Id, resource.Id);
        await harness.Service.RevokeFromClientAsync(client.Id, resource.Id);
        await harness.Service.RevokeFromClientAsync(client.Id, resource.Id);

        Assert.Equal(["association.add", "association.remove"], harness.Calls);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Service.AssignToClientAsync(Guid.NewGuid(), resource.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Service.RevokeFromClientAsync(client.Id, Guid.NewGuid()));
    }

    private sealed class ApiResourceServiceHarness : IDisposable
    {
        private readonly ApiResource resource;
        private readonly OAuthClient? client;
        private readonly HashSet<(Guid ClientId, Guid ResourceId)> associations = [];

        public ApiResourceServiceHarness(ApiResource resource, OAuthClient? client = null)
        {
            this.resource = resource;
            this.client = client;
            var resources = HardeningProxy.Create<IApiResourceRepository>((method, args) => method.Name switch
            {
                nameof(IApiResourceRepository.FindByNameAsync) => null,
                nameof(IApiResourceRepository.FindByAudienceAsync) when (string)args![0]! == resource.Audience => resource,
                nameof(IApiResourceRepository.FindByIdAsync) when (Guid)args![0]! == resource.Id => resource,
                nameof(IApiResourceRepository.AddAsync) => Record("resource.add"),
                nameof(IApiResourceRepository.UpdateAsync) => Record("resource.update"),
                nameof(IApiResourceRepository.DeleteAsync) => Record("resource.delete"),
                _ => null,
            });
            var clients = HardeningProxy.Create<IOAuthClientRepository>((method, args) => method.Name switch
            {
                nameof(IOAuthClientRepository.GetByIdAsync) when client is not null && (Guid)args![0]! == client.Id => client,
                _ => null,
            });
            var associationRepository = HardeningProxy.Create<IClientApiResourceRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IClientApiResourceRepository.GetResourcesByClientIdAsync):
                        var clientId = (Guid)args![0]!;
                        return (IReadOnlyList<ApiResource>)[.. associations
                            .Where(pair => pair.ClientId == clientId)
                            .Select(pair => pair.ResourceId == resource.Id ? resource : null)
                            .Where(value => value is not null)!];
                    case nameof(IClientApiResourceRepository.AddAsync):
                        var association = (ClientApiResource)args![0]!;
                        if (associations.Add((association.ClientId, association.ApiResourceId)))
                            return Record("association.add");
                        return null;
                    case nameof(IClientApiResourceRepository.RemoveAsync):
                        var key = ((Guid)args![0]!, (Guid)args![1]!);
                        if (associations.Remove(key))
                            return Record("association.remove");
                        return null;
                    default:
                        return null;
                }
            });

            Service = new ApiResourceService(resources, associationRepository, clients);

            object? Record(string call)
            {
                Calls.Add(call);
                return null;
            }
        }

        public ApiResourceService Service { get; }

        public List<string> Calls { get; } = [];

        public void Dispose()
        {
        }
    }

    private sealed class ClientServiceHarness : IDisposable
    {
        private readonly ServiceProvider serviceProvider;

        public ClientServiceHarness(OAuthClient? client = null, ApiResource? resource = null)
        {
            var clients = HardeningProxy.Create<IOAuthClientRepository>((method, args) => method.Name switch
            {
                nameof(IOAuthClientRepository.FindByClientIdAsync) when client is not null && (string)args![0]! == client.ClientId => client,
                nameof(IOAuthClientRepository.GetByIdAsync) when client is not null && (Guid)args![0]! == client.Id => client,
                nameof(IOAuthClientRepository.AddAsync) => Record("client.add"),
                _ => null,
            });
            var resources = HardeningProxy.Create<IApiResourceRepository>((method, args) => method.Name switch
            {
                nameof(IApiResourceRepository.FindByIdsAsync) => resource is null
                    ? (IReadOnlyList<ApiResource>)[]
                    : (IReadOnlyList<ApiResource>)[resource],
                nameof(IApiResourceRepository.FindByAudienceAsync) when resource is not null && (string)args![0]! == resource.Audience => resource,
                _ => null,
            });
            var associations = HardeningProxy.Create<IClientApiResourceRepository>((method, _) => method.Name switch
            {
                nameof(IClientApiResourceRepository.GetResourcesByClientIdAsync) => (IReadOnlyList<ApiResource>)[],
                _ => null,
            });
            var secrets = HardeningProxy.Create<IOAuthClientSecretRepository>((_, _) => null);
            var refreshTokens = HardeningProxy.Create<IRefreshTokenRepository>((_, _) => null);
            var blacklist = HardeningProxy.Create<ITokenBlacklistRepository>((_, _) => null);
            serviceProvider = new ServiceCollection()
                .AddSingleton(blacklist)
                .AddScopeKeyedServices(typeof(ClientService).Assembly)
                .BuildServiceProvider();

            Service = new ClientService(
                clients,
                resources,
                secrets,
                associations,
                refreshTokens,
                serviceProvider,
                NullLogger<ClientService>.Instance);

            object? Record(string call)
            {
                Calls.Add(call);
                return null;
            }
        }

        public ClientService Service { get; }

        public List<string> Calls { get; } = [];

        public void Dispose() => serviceProvider.Dispose();
    }
}
