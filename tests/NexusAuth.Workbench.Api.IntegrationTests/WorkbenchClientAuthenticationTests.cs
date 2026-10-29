using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Clients;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Extension;
using NexusAuth.Shared.OAuth;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchClientAuthenticationTests
{
    private const string Authority = "https://login.example.test/identity";
    private const string ExpectedClientId = "client+id%: \u7528\u6237";
    private const string ExpectedClientSecret = "secret+value%: \u7a7a\u683c\U0001F511";

    [Theory]
    [InlineData("exchange")]
    [InlineData("refresh")]
    [InlineData("introspection")]
    public async Task Basic_credentials_are_form_encoded_before_base64_and_roundtrip_through_host_parser(
        string operation)
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        using var provider = CreateProvider(httpClient);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>();

        switch (operation)
        {
            case "exchange":
                await service.ExchangeCodeAsync("authorization-code", "code-verifier", CancellationToken.None);
                break;
            case "refresh":
                await service.RefreshTokensAsync("refresh-token", CancellationToken.None);
                break;
            case "introspection":
                await service.IntrospectAccessTokenAsync("access-token", CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        var authorization = Assert.Single(handler.AuthorizationHeaders);
        Assert.Equal("Basic", authorization.Scheme);
        var encodedCredentials = Encoding.UTF8.GetString(
            Convert.FromBase64String(Assert.IsType<string>(authorization.Parameter)));
        Assert.Equal(
            $"{WebUtility.UrlEncode(ExpectedClientId)}:{WebUtility.UrlEncode(ExpectedClientSecret)}",
            encodedCredentials);

        var parsed = OAuthClientAuthenticationParser.ResolveClientAuthentication(
            authorization.ToString(),
            formClientId: null,
            formClientSecret: null);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(ExpectedClientId, parsed.Authentication!.ClientId);
        Assert.Equal(ExpectedClientSecret, parsed.Authentication.ClientSecret);
        Assert.Equal(
            OAuthClientAuthenticationMethods.ClientSecretBasic,
            parsed.Authentication.TokenEndpointAuthMethod);
    }

    [Fact]
    public async Task Bootstrap_and_basic_authentication_use_the_same_normalized_credentials()
    {
        OAuthClient? client = null;
        ApiResource? resource = null;
        string? bootstrappedClientId = null;
        var links = new Dictionary<Guid, List<Guid>>();
        var clientRepository = CreateProxy<IOAuthClientRepository>((method, args) =>
        {
            switch (method.Name)
            {
                case "FindByClientIdAsync":
                    bootstrappedClientId = (string)args![0]!;
                    return Task.FromResult(client);
                case "AddAsync":
                    client = (OAuthClient)args![0]!;
                    return Task.CompletedTask;
                case "UpdateAsync":
                    return Task.CompletedTask;
                case "ReplaceSharedSecretAsync":
                    client!.ClientSecrets.Clear();
                    client.ClientSecrets.Add((OAuthClientSecret)args![1]!);
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(method.Name);
            }
        });
        var resourceRepository = CreateProxy<IApiResourceRepository>((method, args) => method.Name switch
        {
            "FindByNameAsync" => Task.FromResult(resource),
            "AddAsync" => AddResource((ApiResource)args![0]!),
            "UpdateAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });
        Task AddResource(ApiResource value)
        {
            resource = value;
            return Task.CompletedTask;
        }

        var resourceLinks = CreateProxy<IClientApiResourceRepository>((method, args) =>
        {
            if (method.Name == "GetApiResourceIdsByClientIdsAsync")
                return Task.FromResult(links);
            if (method.Name == "AddAsync")
            {
                links[client!.Id] = [resource!.Id];
                return Task.CompletedTask;
            }

            throw new NotSupportedException(method.Name);
        });

        using var provider = new ServiceCollection()
            .AddSingleton(clientRepository)
            .AddSingleton(resourceRepository)
            .AddSingleton(resourceLinks)
            .BuildServiceProvider();
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:ClientId"] = Raw(ExpectedClientId),
            ["Auth:ClientSecret"] = Raw(ExpectedClientSecret),
            ["Auth:Audience"] = "workbench.api",
            ["Auth:RedirectUri"] = "https://app.test/signin-oidc",
            ["Auth:PostLogoutRedirectUri"] = "https://app.test/",
        });
        var bootstrap = new WorkbenchClientCredentialHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            Options.Create(new WorkbenchBootstrapOptions
            {
                ResourceName = "workbench.api",
                ResourceDisplayName = "Workbench API",
                ClientName = "Workbench",
            }),
            new ProductionEnvironment(),
            NullLogger<WorkbenchClientCredentialHostedService>.Instance);

        await bootstrap.StartAsync(CancellationToken.None);

        Assert.Equal(ExpectedClientId, bootstrappedClientId);
        Assert.Equal(ExpectedClientId, client!.ClientId);
        Assert.True(client.VerifyClientSecret(ExpectedClientSecret));
        Assert.False(client.VerifyClientSecret(Raw(ExpectedClientSecret)));

        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        using var oidcProvider = CreateProvider(httpClient);
        using var scope = oidcProvider.CreateScope();
        var oidc = scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>();
        await oidc.ExchangeCodeAsync("authorization-code", "code-verifier", CancellationToken.None);

        var parsed = OAuthClientAuthenticationParser.ResolveClientAuthentication(
            Assert.Single(handler.AuthorizationHeaders).ToString(),
            formClientId: null,
            formClientSecret: null);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(client.ClientId, parsed.Authentication!.ClientId);
        Assert.True(client.VerifyClientSecret(parsed.Authentication.ClientSecret!));
    }

    private static ServiceProvider CreateProvider(HttpClient client)
    {
        var services = new ServiceCollection();
        services.AddNexusAuth(options =>
        {
            options.Authority = Authority;
            options.ClientId = Raw(ExpectedClientId);
            options.ClientSecret = Raw(ExpectedClientSecret);
            options.RedirectUri = "https://app.test/signin-oidc";
            options.PostLogoutRedirectUri = "https://app.test/";
            options.Scope = OidcScopes.OpenId + " " + OidcScopes.Profile;
        }).UseInMemory();
        services.AddSingleton<IHttpClientFactory>(new SingleClientFactory(client));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static string Raw(string value) => $" \t{value}\n ";

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, RepositoryProxy>();
        ((RepositoryProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<AuthenticationHeaderValue> AuthorizationHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is not null)
                AuthorizationHeaders.Add(request.Headers.Authorization);

            if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
                return JsonResponse(DiscoveryJson);

            if (request.RequestUri.AbsolutePath.EndsWith("/connect/introspect", StringComparison.Ordinal))
                return JsonResponse("""{"active":true,"sub":"user-1","client_id":"client-id"}""");

            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return requestBody.Contains(
                "grant_type=" + OAuthGrantTypes.AuthorizationCode,
                StringComparison.Ordinal)
                ? JsonResponse("""{"access_token":"access","refresh_token":"refresh","id_token":"id","expires_in":120}""")
                : JsonResponse("""{"access_token":"access","refresh_token":"refresh","expires_in":120}""");
        }

        private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public class RepositoryProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args);
    }

    private const string DiscoveryJson = """
        {
          "issuer":"https://login.example.test/identity",
          "token_endpoint":"https://login.example.test/identity/connect/token",
          "introspection_endpoint":"https://login.example.test/identity/connect/introspect"
        }
        """;
}
