using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Workbench.Api;
using NexusAuth.Workbench.Api.Authorization;
using NexusAuth.Workbench.Api.Controllers;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class ControllerAuthorizationPipelineTests
{
    private static readonly Guid AdministratorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrdinaryUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InactiveUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TargetUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid CredentialId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static IEnumerable<object?[]> NonAdministratorActors()
    {
        yield return ["ordinary", ClaimTypes.NameIdentifier, OrdinaryUserId.ToString()];
        yield return ["inactive system account", "sub", InactiveUserId.ToString()];
        yield return ["missing subject", null, null];
        yield return ["invalid subject", ClaimTypes.NameIdentifier, "not-a-guid"];
        yield return ["unknown subject", ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()];
    }

    [Theory]
    [MemberData(nameof(NonAdministratorActors))]
    public async Task Management_endpoints_reject_non_administrators_without_business_calls(
        string actorType,
        string? subjectClaimType,
        string? subject)
    {
        var users = new Dictionary<Guid, User>
        {
            [AdministratorId] = CreateUser("administrator", system: true),
            [OrdinaryUserId] = CreateUser("ordinary"),
            [InactiveUserId] = CreateUser("inactive", system: true, active: false),
            [TargetUserId] = CreateUser("target"),
        };
        var managementCalls = new List<string>();
        var passkeyCalls = new List<string>();
        var userRepository = RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
            method.Name == nameof(IUserRepository.FindByIdAsync) && args![0] is Guid id
                ? users.GetValueOrDefault(id)
                : null);
        var management = RecordingDispatchProxy.Create<IUserManagementService>((method, _) =>
        {
            managementCalls.Add(method.Name);
            return null;
        });
        var passkeys = RecordingDispatchProxy.Create<IPasskeyManagementService>((method, _) =>
        {
            passkeyCalls.Add(method.Name);
            return null;
        });

        using var host = await new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
                services.AddWorkbenchAuthorization();
                // This isolated middleware host doesn't load WorkbenchApiModule's automatic injection.
                services.AddScoped<IWorkbenchPermissionService, MockWorkbenchPermissionService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "test";
                        options.DefaultChallengeScheme = "test";
                        options.DefaultForbidScheme = "test";
                    })
                    .AddScheme<AuthenticationSchemeOptions, ClaimsAuthenticationHandler>("test", _ => { });
                services.AddSingleton(new TestAuthenticationState(subjectClaimType, subject));
                services.AddSingleton(userRepository);
                services.AddSingleton(management);
                services.AddSingleton(passkeys);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            })).StartAsync();
        using var client = host.GetTestClient();

        await AssertForbidden(client, HttpMethod.Get, "/api/users");
        await AssertForbidden(client, HttpMethod.Get, $"/api/users/{TargetUserId}");
        await AssertForbidden(client, HttpMethod.Post, "/api/users",
            """{"username":"managed","password":"Sup3rSecret!","nickname":"Managed"}""");
        await AssertForbidden(client, HttpMethod.Put, $"/api/users/{TargetUserId}",
            """{"nickname":"Updated"}""");
        await AssertForbidden(client, HttpMethod.Patch, $"/api/users/{TargetUserId}/status",
            """{"isActive":true}""");
        await AssertForbidden(client, HttpMethod.Post, $"/api/users/{TargetUserId}/reset-password",
            """{"newPassword":"NewPassw0rd!"}""");
        await AssertForbidden(client, HttpMethod.Delete, $"/api/users/{TargetUserId}");
        await AssertForbidden(client, HttpMethod.Post, $"/api/users/{TargetUserId}/revoke-tokens");
        await AssertForbidden(client, HttpMethod.Get, $"/api/users/{TargetUserId}/passkeys");
        await AssertForbidden(client, HttpMethod.Post,
            $"/api/users/{TargetUserId}/passkeys/{CredentialId}/revoke",
            """{"reason":"security review"}""");

        Assert.True(managementCalls.Count == 0, $"{actorType} reached IUserManagementService.");
        Assert.True(passkeyCalls.Count == 0, $"{actorType} reached IPasskeyManagementService.");
    }

    private static async Task AssertForbidden(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static User CreateUser(string username, bool system = false, bool active = true)
    {
        var user = User.Create(username, "test-password", username);
        if (!active)
            user.UpdateScimProfile(user.Username, user.Nickname, isActive: false);
        if (system)
            user.MarkAsSystemAccount();
        return user;
    }

    private sealed record TestAuthenticationState(string? ClaimType, string? Subject);

    private sealed class ClaimsAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestAuthenticationState state)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>();
            if (state.ClaimType is not null && state.Subject is not null)
                claims.Add(new Claim(state.ClaimType, state.Subject));

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
