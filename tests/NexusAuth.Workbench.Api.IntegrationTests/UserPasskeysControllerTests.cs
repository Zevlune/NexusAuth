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
    public async Task Ordinary_account_cannot_view_or_revoke_passkeys()
    {
        var ordinaryUser = CreateUser("ordinary");
        var target = CreateUser("target");
        var harness = new Harness([ordinaryUser, target]);
        harness.SetActor(ordinaryUser.Id, ClaimTypes.NameIdentifier);

        var get = await harness.Controller.Get(target.Id, CancellationToken.None);
        var revoke = await harness.Controller.Revoke(target.Id, Guid.NewGuid(), new RevokePasskeyRequest("security review"), CancellationToken.None);

        Assert.IsType<ForbidResult>(get);
        Assert.IsType<ForbidResult>(revoke);
        Assert.Empty(harness.PasskeyCalls);
    }

    [Fact]
    public async Task Disabled_system_account_cannot_view_or_revoke_passkeys()
    {
        var disabledAdmin = CreateUser("disabled-admin", system: true, active: false);
        var target = CreateUser("target");
        var harness = new Harness([disabledAdmin, target]);
        harness.SetActor(disabledAdmin.Id, "sub");

        var get = await harness.Controller.Get(target.Id, CancellationToken.None);
        var revoke = await harness.Controller.Revoke(target.Id, Guid.NewGuid(), new RevokePasskeyRequest("security review"), CancellationToken.None);

        Assert.IsType<ForbidResult>(get);
        Assert.IsType<ForbidResult>(revoke);
        Assert.Empty(harness.PasskeyCalls);
    }

    [Fact]
    public async Task Missing_subject_cannot_view_or_revoke_passkeys()
    {
        var admin = CreateUser("admin", system: true);
        var target = CreateUser("target");
        var harness = new Harness([admin, target]);
        harness.SetActor(Guid.NewGuid(), "other", includeSubject: false);

        var get = await harness.Controller.Get(target.Id, CancellationToken.None);
        var revoke = await harness.Controller.Revoke(target.Id, Guid.NewGuid(), new RevokePasskeyRequest("security review"), CancellationToken.None);

        Assert.IsType<ForbidResult>(get);
        Assert.IsType<ForbidResult>(revoke);
        Assert.Empty(harness.PasskeyCalls);
    }

    [Fact]
    public async Task Active_system_account_can_view_safe_summary_without_cache()
    {
        var admin = CreateUser("admin", system: true);
        var target = CreateUser("target");
        var summaries = new[]
        {
            new WebAuthnCredentialSummary(Guid.NewGuid(), "Laptop", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, null, true, true),
        };
        var harness = new Harness([admin, target]) { Summaries = summaries };
        harness.SetActor(admin.Id, "sub");

        var result = await harness.Controller.Get(target.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(summaries, Assert.IsAssignableFrom<IReadOnlyList<WebAuthnCredentialSummary>>(ok.Value));
        Assert.Equal("no-store", harness.Controller.Response.Headers.CacheControl);
        Assert.Equal(target.Id, Assert.Single(harness.PasskeyCalls).Arguments![0]);
    }

    [Fact]
    public async Task Active_system_account_can_revoke_and_controller_passes_name_identifier_as_actor()
    {
        var admin = CreateUser("admin", system: true);
        var target = CreateUser("target");
        var credentialId = Guid.NewGuid();
        var harness = new Harness([admin, target]) { RevokeResult = true };
        harness.SetActor(admin.Id, ClaimTypes.NameIdentifier);

        var result = await harness.Controller.Revoke(target.Id, credentialId,
            new RevokePasskeyRequest("Reported lost"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var call = Assert.Single(harness.PasskeyCalls);
        Assert.Equal(nameof(IPasskeyManagementService.RevokeAsync), call.Method.Name);
        Assert.Equal(target.Id, call.Arguments![0]);
        Assert.Equal(credentialId, call.Arguments[1]);
        Assert.Equal(admin.Id, call.Arguments[2]);
        Assert.Equal("Reported lost", call.Arguments[3]);
    }

    [Fact]
    public async Task Missing_user_and_missing_or_disabled_credential_return_not_found()
    {
        var admin = CreateUser("admin", system: true);
        var target = CreateUser("target");
        var harness = new Harness([admin, target]) { RevokeResult = false };
        harness.SetActor(admin.Id, ClaimTypes.NameIdentifier);

        var missingUser = await harness.Controller.Get(Guid.NewGuid(), CancellationToken.None);
        var missingCredential = await harness.Controller.Revoke(target.Id, Guid.NewGuid(),
            new RevokePasskeyRequest("Reported lost"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(missingUser);
        Assert.IsType<NotFoundResult>(missingCredential);
    }

    [Fact]
    public async Task Invalid_reason_is_returned_as_bad_request()
    {
        var admin = CreateUser("admin", system: true);
        var target = CreateUser("target");
        var harness = new Harness([admin, target])
        {
            RevokeException = new ArgumentException("撤销原因无效。", "reason"),
        };
        harness.SetActor(admin.Id, ClaimTypes.NameIdentifier);

        var result = await harness.Controller.Revoke(target.Id, Guid.NewGuid(),
            new RevokePasskeyRequest("invalid"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
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
