using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Workbench.Api.Controllers;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class UserRequestValidationTests
{
    private const string UserPath = "/api/users/11111111-1111-1111-1111-111111111111";
    private const string RevokePasskeyPath = UserPath + "/passkeys/22222222-2222-2222-2222-222222222222/revoke";

    public static IEnumerable<object[]> Requests()
    {
        yield return ["POST", "/api/users", """{"username":"alice","password":"TestPassw0rd!","nickname":"Alice"}""", HttpStatusCode.OK];
        yield return ["PUT", UserPath, """{"nickname":"Alice"}""", HttpStatusCode.OK];
        yield return ["POST", UserPath + "/reset-password", """{"newPassword":"NewPassw0rd!"}""", HttpStatusCode.NoContent];

        foreach (var (field, value) in new (string, string?)[]
        {
            ("username", null), ("username", ""), ("username", new string('u', 101)),
            ("password", ""), ("password", new string('p', 73)), ("nickname", ""),
            ("email", "invalid-email"), ("phoneNumber", new string('1', 21)), ("timezone", new string('t', 101)),
        })
        {
            var payload = new Dictionary<string, object?>
            {
                ["username"] = "alice", ["password"] = "TestPassw0rd!", ["nickname"] = "Alice", [field] = value,
            };
            yield return ["POST", "/api/users", JsonSerializer.Serialize(payload), HttpStatusCode.BadRequest];
        }

        foreach (var (field, value) in new[] { ("nickname", ""), ("email", "invalid-email"), ("timezone", new string('t', 101)) })
        {
            var payload = new Dictionary<string, string> { ["nickname"] = "Alice", [field] = value };
            yield return ["PUT", UserPath, JsonSerializer.Serialize(payload), HttpStatusCode.BadRequest];
        }

        foreach (var password in new[] { "", new string('p', 73) })
            yield return ["POST", UserPath + "/reset-password", JsonSerializer.Serialize(new { newPassword = password }), HttpStatusCode.BadRequest];

        // The Passkey controller wraps successful responses as an API result with status 200.
        yield return ["POST", RevokePasskeyPath, """{"reason":"Lost device"}""", HttpStatusCode.OK];
        yield return ["POST", RevokePasskeyPath, JsonSerializer.Serialize(new { reason = new string('r', 500) }), HttpStatusCode.OK];
        foreach (var reason in new string?[] { null, "", "   ", new string('r', 501) })
            yield return ["POST", RevokePasskeyPath, JsonSerializer.Serialize(new { reason }), HttpStatusCode.BadRequest];
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task User_requests_are_validated_through_the_http_pipeline(string method, string path, string json, HttpStatusCode expectedStatus)
    {
        var serviceCalls = new List<string>();
        var administrator = User.Create("admin", "test-password", "Administrator");
        administrator.MarkAsSystemAccount();
        var userRepository = RecordingDispatchProxy.Create<IUserRepository>((member, _) =>
            member.Name == nameof(IUserRepository.FindByIdAsync) ? administrator : null);
        var dto = new ManagedUserDto(Guid.NewGuid(), "alice", "Alice", null, null, true,
            null, null, null, null, null, null, null, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var management = RecordingDispatchProxy.Create<IUserManagementService>((member, _) =>
        {
            serviceCalls.Add(member.Name);
            return member.Name == nameof(IUserManagementService.ResetPasswordAsync) ? null : dto;
        });
        var passkeys = RecordingDispatchProxy.Create<IPasskeyManagementService>((member, _) =>
        {
            serviceCalls.Add(member.Name);
            return true;
        });

        using var host = await new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
                services.AddAuthorization();
                services.AddAuthentication("test")
                    .AddScheme<AuthenticationSchemeOptions, AdministratorAuthenticationHandler>("test", _ => { });
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
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedStatus == HttpStatusCode.BadRequest)
        {
            Assert.Empty(serviceCalls);
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
            Assert.NotNull(problem);
            Assert.NotEmpty(problem.Errors);
        }
        else
        {
            Assert.Single(serviceCalls);
        }
    }

    private sealed class AdministratorAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
