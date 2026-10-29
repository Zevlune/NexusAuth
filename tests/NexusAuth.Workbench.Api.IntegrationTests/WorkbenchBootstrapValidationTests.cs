using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAuth.Workbench.Api;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchBootstrapValidationTests
{
    [Fact]
    public async Task Bootstrap_is_idempotent_across_repeated_starts()
    {
        OAuthClient? client = null;
        ApiResource? resource = null;
        var linkedResources = new Dictionary<Guid, List<Guid>>();
        var secretWrites = 0;
        var clientRepository = CreateRepository<IOAuthClientRepository>((method, args) =>
        {
            switch (method.Name)
            {
                case "FindByClientIdAsync": return Task.FromResult(client);
                case "AddAsync": client = (OAuthClient)args![0]!; return Task.CompletedTask;
                case "UpdateAsync": return Task.CompletedTask;
                case "ReplaceSharedSecretAsync":
                    client!.ClientSecrets.Clear();
                    client.ClientSecrets.Add((OAuthClientSecret)args![1]!);
                    secretWrites++;
                    return Task.CompletedTask;
                default: throw new NotSupportedException(method.Name);
            }
        });
        var resourceRepository = CreateRepository<IApiResourceRepository>((method, args) => method.Name switch
        {
            "FindByNameAsync" => Task.FromResult(resource),
            "AddAsync" => AddResource((ApiResource)args![0]!),
            "UpdateAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        });
        Task AddResource(ApiResource value) { resource = value; return Task.CompletedTask; }
        var associations = CreateRepository<IClientApiResourceRepository>((method, args) =>
        {
            if (method.Name == "GetApiResourceIdsByClientIdsAsync")
                return Task.FromResult(linkedResources);
            if (method.Name == "AddAsync")
            {
                linkedResources[client!.Id] = [resource!.Id];
                return Task.CompletedTask;
            }
            throw new NotSupportedException(method.Name);
        });
        using var provider = new ServiceCollection()
            .AddSingleton(clientRepository).AddSingleton(resourceRepository).AddSingleton(associations)
            .BuildServiceProvider();
        var service = new WorkbenchClientCredentialHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkbenchAuthenticationOptions
            {
                ClientId = "workbench",
                ClientSecret = "bootstrap-secret",
                Audience = "workbench.api",
                RedirectUri = "https://app.test/signin-oidc",
                PostLogoutRedirectUri = "https://app.test/"
            }),
            Options.Create(new WorkbenchBootstrapOptions
            {
                ResourceName = "workbench.api", ResourceDisplayName = "Workbench API", ClientName = "Workbench"
            }), new ProductionEnvironment(), NullLogger<WorkbenchClientCredentialHostedService>.Instance);

        await service.StartAsync(default);
        var clientId = client!.Id;
        var resourceId = resource!.Id;
        Assert.Equal(
            new[] { OAuthGrantTypes.AuthorizationCode, OAuthGrantTypes.RefreshToken },
            client.AllowedGrantTypes);

        await service.StartAsync(default);
        Assert.Equal(clientId, client.Id);
        Assert.Equal(resourceId, resource.Id);
        Assert.Equal(new[] { "https://app.test/signin-oidc" }, client.RedirectUris);
        Assert.Equal(new[] { "https://app.test/" }, client.PostLogoutRedirectUris);
        Assert.Equal(
            new[] { OAuthGrantTypes.AuthorizationCode, OAuthGrantTypes.RefreshToken },
            client.AllowedGrantTypes);
        Assert.Equal(
            new[] { OidcScopes.OpenId, OidcScopes.Profile, OidcScopes.OfflineAccess, "workbench.api" },
            client.AllowedScopes);
        Assert.True(client.RequirePkce);
        Assert.Equal(OAuthClientAuthenticationMethods.ClientSecretBasic, client.TokenEndpointAuthMethod);
        Assert.Single(linkedResources[clientId]);
        Assert.Equal(1, secretWrites);
    }

    [Fact]
    public async Task Bootstrap_updates_an_existing_client_to_the_workbench_configuration()
    {
        var client = OAuthClient.Create(
            "workbench",
            "Legacy Workbench",
            description: "Legacy description",
            redirectUris: ["https://legacy.test/callback"],
            postLogoutRedirectUris: ["https://legacy.test/"],
            allowedScopes: ["legacy.api"],
            allowedGrantTypes: [OAuthGrantTypes.ClientCredentials],
            requirePkce: false,
            tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.ClientSecretPost);
        ApiResource? resource = null;
        var linkedResources = new Dictionary<Guid, List<Guid>>();
        var updateWrites = 0;
        var secretWrites = 0;
        var clientRepository = CreateRepository<IOAuthClientRepository>((method, args) =>
        {
            switch (method.Name)
            {
                case "FindByClientIdAsync": return Task.FromResult<OAuthClient?>(client);
                case "UpdateAsync": updateWrites++; return Task.CompletedTask;
                case "ReplaceSharedSecretAsync":
                    client.ClientSecrets.Clear();
                    client.ClientSecrets.Add((OAuthClientSecret)args![1]!);
                    secretWrites++;
                    return Task.CompletedTask;
                default: throw new NotSupportedException(method.Name);
            }
        });
        var resourceRepository = CreateRepository<IApiResourceRepository>((method, args) => method.Name switch
        {
            "FindByNameAsync" => Task.FromResult(resource),
            "AddAsync" => AddResource((ApiResource)args![0]!),
            "UpdateAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        });
        Task AddResource(ApiResource value) { resource = value; return Task.CompletedTask; }
        var associations = CreateRepository<IClientApiResourceRepository>((method, args) =>
        {
            if (method.Name == "GetApiResourceIdsByClientIdsAsync")
                return Task.FromResult(linkedResources);
            if (method.Name == "AddAsync")
            {
                linkedResources[client.Id] = [resource!.Id];
                return Task.CompletedTask;
            }
            throw new NotSupportedException(method.Name);
        });
        using var provider = new ServiceCollection()
            .AddSingleton(clientRepository).AddSingleton(resourceRepository).AddSingleton(associations)
            .BuildServiceProvider();
        var service = new WorkbenchClientCredentialHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkbenchAuthenticationOptions
            {
                ClientId = "workbench",
                ClientSecret = "updated-secret",
                Audience = "workbench.api",
                RedirectUri = "https://app.test/signin-oidc",
                PostLogoutRedirectUri = "https://app.test/"
            }),
            Options.Create(new WorkbenchBootstrapOptions
            {
                ResourceName = "workbench.api",
                ResourceDisplayName = "Workbench API",
                ResourceDescription = "Managed resource",
                ClientName = "Workbench",
                ClientDescription = "Managed client"
            }),
            new ProductionEnvironment(),
            NullLogger<WorkbenchClientCredentialHostedService>.Instance);

        await service.StartAsync(default);

        Assert.Equal(1, updateWrites);
        Assert.Equal(1, secretWrites);
        Assert.Equal("Workbench", client.ClientName);
        Assert.Equal("Managed client", client.Description);
        Assert.Equal(new[] { "https://app.test/signin-oidc" }, client.RedirectUris);
        Assert.Equal(new[] { "https://app.test/" }, client.PostLogoutRedirectUris);
        Assert.Equal(
            new[] { OAuthGrantTypes.AuthorizationCode, OAuthGrantTypes.RefreshToken },
            client.AllowedGrantTypes);
        Assert.Equal(
            new[] { OidcScopes.OpenId, OidcScopes.Profile, OidcScopes.OfflineAccess, "workbench.api" },
            client.AllowedScopes);
        Assert.True(client.RequirePkce);
        Assert.True(client.IsActive);
        Assert.Equal(OAuthClientAuthenticationMethods.ClientSecretBasic, client.TokenEndpointAuthMethod);
        Assert.True(client.VerifyClientSecret("updated-secret"));
        Assert.Single(linkedResources[client.Id]);
    }

    private static T CreateRepository<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, RepositoryProxy>();
        ((RepositoryProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class RepositoryProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    [Fact]
    public async Task Missing_required_configuration_fails_instead_of_skipping_registration()
    {
        var service = new WorkbenchClientCredentialHostedService(
            null!,
            Options.Create(new WorkbenchAuthenticationOptions()),
            Options.Create(new WorkbenchBootstrapOptions()),
            new ProductionEnvironment(), NullLogger<WorkbenchClientCredentialHostedService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.Contains("Bootstrap:ResourceName", exception.Message);
        Assert.Contains("Auth:ClientSecret", exception.Message);
    }

    [Fact]
    public void Authentication_options_pipeline_normalizes_bootstrap_configuration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = " https://issuer.test/identity/ ",
            ["Auth:Audience"] = " workbench.api ",
            ["Auth:ClientId"] = " workbench ",
            ["Auth:ClientSecret"] = " bootstrap-secret ",
            ["Auth:RedirectUri"] = " https://app.test/signin-oidc ",
            ["Auth:PostLogoutRedirectUri"] = " https://app.test/ ",
            ["Auth:Scope"] = " openid profile "
        });
        var services = new ServiceCollection();
        services.AddWorkbenchAuthentication(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<WorkbenchAuthenticationOptions>>().Value;

        Assert.Equal("https://issuer.test/identity", options.Authority);
        Assert.Equal("workbench.api", options.Audience);
        Assert.Equal("workbench", options.ClientId);
        Assert.Equal("bootstrap-secret", options.ClientSecret);
        Assert.Equal("https://app.test/signin-oidc", options.RedirectUri);
        Assert.Equal("https://app.test/", options.PostLogoutRedirectUri);
        Assert.Equal("openid profile workbench.api", options.Scope);
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
