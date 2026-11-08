using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.ApiResources;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Shared.DependencyInjection;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class ManagedResourceProtectionTests
{
    [Fact]
    public void New_clients_and_resources_are_unmanaged_until_marked()
    {
        var client = OAuthClient.Create("managed-test-client", "Managed test client");
        var resource = ApiResource.Create("managed-test-resource", "Managed test resource", "managed.test");

        Assert.False(client.IsSystemManaged);
        Assert.False(resource.IsSystemManaged);

        client.MarkAsSystemManaged();
        client.MarkAsSystemManaged();
        resource.MarkAsSystemManaged();
        resource.MarkAsSystemManaged();

        Assert.True(client.IsSystemManaged);
        Assert.True(resource.IsSystemManaged);
    }

    [Fact]
    public async Task Managed_client_cannot_be_updated()
    {
        var client = OAuthClient.Create("managed-test-client", "Managed test client");
        client.MarkAsSystemManaged();
        using var harness = new ClientServiceHarness(client);

        var exception = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() => harness.Service.UpdateAsync(
            client.Id,
            new UpdateClientRequest("Changed", null, null, null, null, null, null, null, null, null, true)));

        Assert.Contains("Workbench", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task Managed_client_cannot_generate_or_reset_credentials()
    {
        var client = OAuthClient.Create("managed-test-client", "Managed test client");
        var existingSecret = OAuthClientSecret.CreateSharedSecret(client.Id, "existing-secret");
        client.ClientSecrets.Add(existingSecret);
        client.MarkAsSystemManaged();
        using var harness = new ClientServiceHarness(client);

        var generateException = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() =>
            harness.Service.GenerateCredentialAsync(client.Id, new GenerateClientCredentialRequest()));
        var resetException = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() =>
            harness.Service.ResetCredentialAsync(client.Id, new GenerateClientCredentialRequest()));

        Assert.Contains("Workbench", generateException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Workbench", resetException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(existingSecret.IsActive);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task Managed_client_cannot_be_deleted()
    {
        var client = OAuthClient.Create("managed-test-client", "Managed test client");
        client.MarkAsSystemManaged();
        using var harness = new ClientServiceHarness(client);

        var exception = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() => harness.Service.DeleteAsync(client.Id));

        Assert.Contains("Workbench", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task Managed_api_resource_cannot_be_updated_status_changed_or_deleted()
    {
        var resource = ApiResource.Create("managed-test-resource", "Managed test resource", "managed.test");
        resource.MarkAsSystemManaged();
        using var harness = new ApiResourceServiceHarness(resource);

        var updateException = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() => harness.Service.UpdateAsync(
            resource.Id,
            new UpdateApiResourceRequest("Changed", null, null, true)));
        var statusException = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() =>
            harness.Service.UpdateStatusAsync(resource.Id, false));
        var deleteException = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() => harness.Service.DeleteAsync(resource.Id));

        Assert.Contains("Workbench", updateException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Workbench", statusException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Workbench", deleteException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task Managed_api_resource_cannot_be_assigned_or_revoked()
    {
        var resource = ApiResource.Create("managed-test-resource", "Managed test resource", "managed.test");
        resource.MarkAsSystemManaged();
        using var harness = new ApiResourceServiceHarness(resource);
        var clientId = Guid.NewGuid();

        var assignException = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() =>
            harness.Service.AssignToClientAsync(clientId, resource.Id));
        var revokeException = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() =>
            harness.Service.RevokeFromClientAsync(clientId, resource.Id));

        Assert.Contains("Workbench", assignException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Workbench", revokeException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task Resource_assignment_cannot_mutate_a_managed_client()
    {
        var resource = ApiResource.Create("ordinary-test-resource", "Ordinary test resource", "ordinary.test");
        var client = OAuthClient.Create("managed-test-client", "Managed test client");
        client.MarkAsSystemManaged();
        using var harness = new ApiResourceServiceHarness(resource, client);

        var assignException = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() =>
            harness.Service.AssignToClientAsync(client.Id, resource.Id));
        var revokeException = await Assert.ThrowsAsync<WorkbenchBusinessConflictException>(() =>
            harness.Service.RevokeFromClientAsync(client.Id, resource.Id));

        Assert.Contains("Workbench", assignException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Workbench", revokeException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Calls);
    }

    private sealed class ClientServiceHarness : IDisposable
    {
        public ClientServiceHarness(OAuthClient client)
        {
            var clients = HardeningProxy.Create<IOAuthClientRepository>((method, args) => method.Name switch
            {
                nameof(IOAuthClientRepository.GetByIdAsync) when (Guid)args![0]! == client.Id => client,
                nameof(IOAuthClientRepository.UpdateAsync) => Record("client.update"),
                nameof(IOAuthClientRepository.DeleteAsync) => Record("client.delete"),
                _ => null,
            });
            var resources = HardeningProxy.Create<IApiResourceRepository>((_, _) => null);
            var secrets = HardeningProxy.Create<IOAuthClientSecretRepository>((method, _) =>
                method.Name == nameof(IOAuthClientSecretRepository.AddAsync) ? Record("secret.add") : null);
            var associations = HardeningProxy.Create<IClientApiResourceRepository>((method, _) =>
                method.Name is nameof(IClientApiResourceRepository.AddAsync)
                    or nameof(IClientApiResourceRepository.RemoveAsync)
                    or nameof(IClientApiResourceRepository.RemoveByClientIdAsync)
                    ? Record("association.mutate")
                    : null);
            var refreshTokens = HardeningProxy.Create<IRefreshTokenRepository>((method, _) =>
                method.Name == nameof(IRefreshTokenRepository.RevokeAllForClientAsync) ? Record("refresh.revoke") : null);
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

        private readonly ServiceProvider serviceProvider;

        public ClientService Service { get; }

        public List<string> Calls { get; } = [];

        public void Dispose() => serviceProvider.Dispose();
    }

    private sealed class ApiResourceServiceHarness : IDisposable
    {
        public ApiResourceServiceHarness(ApiResource resource, OAuthClient? client = null)
        {
            var resources = HardeningProxy.Create<IApiResourceRepository>((method, args) => method.Name switch
            {
                nameof(IApiResourceRepository.FindByIdAsync) when (Guid)args![0]! == resource.Id => resource,
                nameof(IApiResourceRepository.UpdateAsync) => Record("resource.update"),
                nameof(IApiResourceRepository.DeleteAsync) => Record("resource.delete"),
                _ => null,
            });
            var clients = HardeningProxy.Create<IOAuthClientRepository>((method, args) => method.Name switch
            {
                nameof(IOAuthClientRepository.GetByIdAsync) when client is not null && (Guid)args![0]! == client.Id => client,
                _ => null,
            });
            var associations = HardeningProxy.Create<IClientApiResourceRepository>((method, _) => method.Name switch
            {
                nameof(IClientApiResourceRepository.AddAsync) => Record("association.add"),
                nameof(IClientApiResourceRepository.RemoveAsync) => Record("association.remove"),
                nameof(IClientApiResourceRepository.RemoveByApiResourceIdAsync) => Record("association.remove-resource"),
                _ => null,
            });

            Service = new ApiResourceService(resources, associations, clients);

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
}
