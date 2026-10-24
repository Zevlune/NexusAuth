using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Workbench.Api.Controllers;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class UserPasskeysControllerTests
{
    [Fact]
    public async Task Get_returns_safe_summary_without_cache()
    {
        var target = CreateUser("target");
        var summaries = new[]
        {
            new WebAuthnCredentialSummary(Guid.NewGuid(), "Laptop", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, null, true, true),
        };
        var harness = new Harness([target]) { Summaries = summaries };

        var result = await harness.Controller.Get(target.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(summaries, Assert.IsAssignableFrom<IReadOnlyList<WebAuthnCredentialSummary>>(ok.Value));
        Assert.Equal("no-store", harness.Controller.Response.Headers.CacheControl);
        Assert.Equal(target.Id, Assert.Single(harness.PasskeyCalls).Arguments![0]);
    }

    [Fact]
    public async Task Revoke_passes_name_identifier_as_actor()
    {
        var actor = CreateUser("actor");
        var target = CreateUser("target");
        var credentialId = Guid.NewGuid();
        var harness = new Harness([actor, target]) { RevokeResult = true };
        harness.SetActor(actor.Id, ClaimTypes.NameIdentifier);

        var result = await harness.Controller.Revoke(target.Id, credentialId,
            new RevokePasskeyRequest("Reported lost"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var call = Assert.Single(harness.PasskeyCalls);
        Assert.Equal(nameof(IPasskeyManagementService.RevokeAsync), call.Method.Name);
        Assert.Equal(target.Id, call.Arguments![0]);
        Assert.Equal(credentialId, call.Arguments[1]);
        Assert.Equal(actor.Id, call.Arguments[2]);
        Assert.Equal("Reported lost", call.Arguments[3]);
    }

    [Fact]
    public async Task Missing_user_and_missing_or_disabled_credential_return_not_found()
    {
        var actor = CreateUser("actor");
        var target = CreateUser("target");
        var harness = new Harness([actor, target]) { RevokeResult = false };
        harness.SetActor(actor.Id, ClaimTypes.NameIdentifier);

        var missingUser = await harness.Controller.Get(Guid.NewGuid(), CancellationToken.None);
        var missingCredential = await harness.Controller.Revoke(target.Id, Guid.NewGuid(),
            new RevokePasskeyRequest("Reported lost"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(missingUser);
        Assert.IsType<NotFoundResult>(missingCredential);
    }

    [Fact]
    public async Task Invalid_reason_is_returned_as_bad_request()
    {
        var actor = CreateUser("actor");
        var target = CreateUser("target");
        var harness = new Harness([actor, target])
        {
            RevokeException = new ArgumentException("撤销原因无效。", "reason"),
        };
        harness.SetActor(actor.Id, ClaimTypes.NameIdentifier);

        var result = await harness.Controller.Revoke(target.Id, Guid.NewGuid(),
            new RevokePasskeyRequest("invalid"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static User CreateUser(string username)
    {
        var user = User.Create(username, "test-password", username);
        return user;
    }

    private sealed class Harness
    {
        private readonly Dictionary<Guid, User> users;
        private readonly IUserRepository userRepository;
        private readonly IPasskeyManagementService passkeys;

        public WebAuthnCredentialSummary[] Summaries { get; set; } = [];
        public bool RevokeResult { get; set; }
        public Exception? RevokeException { get; set; }
        public List<RecordedCall> PasskeyCalls { get; } = [];
        public UserPasskeysController Controller { get; }

        public Harness(IEnumerable<User> users)
        {
            this.users = users.ToDictionary(user => user.Id);
            userRepository = RecordingProxy.Create<IUserRepository>((method, args) =>
            {
                if (method.Name == nameof(IUserRepository.FindByIdAsync))
                    return this.users.GetValueOrDefault((Guid)args![0]!);
                return null;
            });
            passkeys = RecordingProxy.Create<IPasskeyManagementService>((method, args) =>
            {
                PasskeyCalls.Add(new RecordedCall(method, args));
                if (method.Name == nameof(IPasskeyManagementService.GetAsync))
                    return Summaries;
                if (method.Name == nameof(IPasskeyManagementService.RevokeAsync))
                {
                    if (RevokeException is not null)
                        throw RevokeException;
                    return RevokeResult;
                }
                return null;
            });
            Controller = new UserPasskeysController(passkeys, userRepository)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }

        public void SetActor(Guid id, string subjectClaim, bool includeSubject = true)
        {
            var claims = new List<Claim>();
            if (includeSubject)
                claims.Add(new Claim(subjectClaim, id.ToString()));
            Controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }
    }

    private sealed record RecordedCall(MethodInfo Method, object?[]? Arguments);

    public class RecordingProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> handler = null!;

        public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, RecordingProxy>();
            ((RecordingProxy)(object)proxy).handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var result = handler(targetMethod, args);
            if (targetMethod.ReturnType == typeof(Task))
                return Task.CompletedTask;
            if (targetMethod.ReturnType.IsGenericType && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result]);
            }
            return result;
        }
    }
}
