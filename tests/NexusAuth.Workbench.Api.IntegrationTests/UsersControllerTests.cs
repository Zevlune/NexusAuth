using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexusAuth.Application;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Workbench.Api.Controllers;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class UsersControllerTests
{
    [Fact]
    public async Task Ordinary_account_is_rejected_for_every_action_without_service_calls()
    {
        var ordinary = CreateUser("ordinary", system: false);
        var harness = new Harness([ordinary]);
        harness.SetActor(ordinary.Id, ClaimTypes.NameIdentifier);

        await AssertAllActionsForbidden(harness);
    }

    [Fact]
    public async Task Disabled_system_account_is_rejected_for_every_action_without_service_calls()
    {
        var disabledAdmin = CreateUser("disabled-admin", system: true, active: false);
        var harness = new Harness([disabledAdmin]);
        harness.SetActor(disabledAdmin.Id, "sub");

        await AssertAllActionsForbidden(harness);
    }

    [Fact]
    public async Task Missing_subject_is_rejected_for_every_action_without_service_calls()
    {
        var admin = CreateUser("admin", system: true);
        var harness = new Harness([admin]);
        harness.SetActor(Guid.NewGuid(), "other", includeSubject: false);

        await AssertAllActionsForbidden(harness);
    }

    [Fact]
    public async Task Invalid_subject_is_rejected_for_every_action_without_service_calls()
    {
        var admin = CreateUser("admin", system: true);
        var harness = new Harness([admin]);
        harness.SetActor(Guid.NewGuid(), ClaimTypes.NameIdentifier, subjectValue: "not-a-guid");

        await AssertAllActionsForbidden(harness);
    }

    [Fact]
    public async Task Unknown_actor_subject_is_rejected_for_every_action_without_service_calls()
    {
        var admin = CreateUser("admin", system: true);
        var harness = new Harness([admin]);
        harness.SetActor(Guid.NewGuid(), ClaimTypes.NameIdentifier);

        await AssertAllActionsForbidden(harness);
    }

    [Fact]
    public async Task Active_system_account_returns_service_results_without_cache()
    {
        var admin = CreateUser("admin", system: true);
        var harness = new Harness([admin]);
        harness.SetActor(admin.Id, ClaimTypes.NameIdentifier);
        var targetId = Guid.NewGuid();

        var paged = await harness.Controller.GetPaged("managed", true, 2, 25, CancellationToken.None);
        Assert.Same(harness.Paged, Assert.IsType<OkObjectResult>(paged).Value);
        Assert.Equal("no-store", harness.Controller.Response.Headers.CacheControl);

        var byId = await harness.Controller.GetById(targetId, CancellationToken.None);
        Assert.Same(harness.Dto, Assert.IsType<OkObjectResult>(byId).Value);

        var created = await harness.Controller.Create(ValidCreate(), CancellationToken.None);
        Assert.Same(harness.Dto, Assert.IsType<OkObjectResult>(created).Value);

        var updated = await harness.Controller.Update(targetId, ValidUpdate(), CancellationToken.None);
        Assert.Same(harness.Dto, Assert.IsType<OkObjectResult>(updated).Value);

        var status = await harness.Controller.UpdateStatus(targetId, new UpdateManagedUserStatusRequest(false), CancellationToken.None);
        Assert.Same(harness.Dto, Assert.IsType<OkObjectResult>(status).Value);

        Assert.IsType<NoContentResult>(await harness.Controller.ResetPassword(
            targetId, new ResetManagedUserPasswordRequest("NewPassw0rd!"), CancellationToken.None));
        Assert.IsType<NoContentResult>(await harness.Controller.Delete(targetId, CancellationToken.None));
        Assert.IsType<NoContentResult>(await harness.Controller.RevokeTokens(targetId, CancellationToken.None));

        Assert.Equal(8, harness.ServiceCalls.Count);
        Assert.Equal("no-store", harness.Controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task GetById_returns_not_found_when_service_returns_null()
    {
        var admin = CreateUser("admin", system: true);
        var harness = new Harness([admin]) { ById = null };
        harness.SetActor(admin.Id, ClaimTypes.NameIdentifier);

        var result = await harness.Controller.GetById(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal("no-store", harness.Controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Service_exceptions_map_to_bad_request_not_found_and_conflict()
    {
        var admin = CreateUser("admin", system: true);
        var harness = new Harness([admin]);
        harness.SetActor(admin.Id, "sub");
        var invalidRequest = new ArgumentException("invalid username", "request");
        var missingUser = new KeyNotFoundException("user not found");
        var protectedUser = new InvalidOperationException("System accounts cannot be deleted.");
        harness.Exceptions[nameof(IUserManagementService.CreateAsync)] = invalidRequest;
        harness.Exceptions[nameof(IUserManagementService.UpdateProfileAsync)] = missingUser;
        harness.Exceptions[nameof(IUserManagementService.DeleteAsync)] = protectedUser;

        var badRequest = Assert.IsType<BadRequestObjectResult>(
            await harness.Controller.Create(ValidCreate(), CancellationToken.None));
        Assert.Equal(invalidRequest.Message, MessageOf(badRequest.Value));

        var notFound = Assert.IsType<NotFoundObjectResult>(
            await harness.Controller.Update(Guid.NewGuid(), ValidUpdate(), CancellationToken.None));
        Assert.Equal(missingUser.Message, MessageOf(notFound.Value));

        var conflict = Assert.IsType<ConflictObjectResult>(
            await harness.Controller.Delete(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(protectedUser.Message, MessageOf(conflict.Value));

        Assert.Equal("no-store", harness.Controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Create_response_does_not_expose_passwords()
    {
        var admin = CreateUser("admin", system: true);
        var harness = new Harness([admin]);
        harness.SetActor(admin.Id, ClaimTypes.NameIdentifier);

        var result = await harness.Controller.Create(ValidCreate(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(ManagedUserDto).GetProperties(),
            property => property.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertAllActionsForbidden(Harness harness)
    {
        var actions = AllActions(harness.Controller).ToArray();
        Assert.Equal(8, actions.Length);
        foreach (var action in actions)
            Assert.IsType<ForbidResult>(await action());
        Assert.Empty(harness.ServiceCalls);
    }

    private static IEnumerable<Func<Task<IActionResult>>> AllActions(UsersController controller)
    {
        yield return () => controller.GetPaged(null, null, 1, 10, CancellationToken.None);
        yield return () => controller.GetById(Guid.NewGuid(), CancellationToken.None);
        yield return () => controller.Create(ValidCreate(), CancellationToken.None);
        yield return () => controller.Update(Guid.NewGuid(), ValidUpdate(), CancellationToken.None);
        yield return () => controller.UpdateStatus(Guid.NewGuid(), new UpdateManagedUserStatusRequest(true), CancellationToken.None);
        yield return () => controller.ResetPassword(Guid.NewGuid(), new ResetManagedUserPasswordRequest("NewPassw0rd!"), CancellationToken.None);
        yield return () => controller.Delete(Guid.NewGuid(), CancellationToken.None);
        yield return () => controller.RevokeTokens(Guid.NewGuid(), CancellationToken.None);
    }

    private static string? MessageOf(object? value) =>
        value?.GetType().GetProperty("message")?.GetValue(value) as string;

    private static CreateManagedUserRequest ValidCreate() =>
        new("managed", "Sup3rSecret!", "Managed");

    private static UpdateManagedUserRequest ValidUpdate() =>
        new("Updated", null, null, null, null, null, null, null, null, null);

    private static User CreateUser(string username, bool system, bool active = true)
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

        public Harness(IEnumerable<User> users)
        {
            this.users = users.ToDictionary(user => user.Id);
            var userRepository = RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
            {
                if (method.Name == nameof(IUserRepository.FindByIdAsync))
                    return this.users.GetValueOrDefault((Guid)args![0]!);
                return null;
            });
            var management = RecordingDispatchProxy.Create<IUserManagementService>((method, args) =>
            {
                ServiceCalls.Add(new RecordedProxyCall("service", method, args));
                if (Exceptions.TryGetValue(method.Name, out var exception) && exception is not null)
                    throw exception;
                return method.Name switch
                {
                    nameof(IUserManagementService.GetPagedAsync) => Paged,
                    nameof(IUserManagementService.GetByIdAsync) => ById,
                    nameof(IUserManagementService.CreateAsync) => Dto,
                    nameof(IUserManagementService.UpdateProfileAsync) => Dto,
                    nameof(IUserManagementService.UpdateStatusAsync) => Dto,
                    _ => null,
                };
            });
            Dto = new ManagedUserDto(
                Guid.NewGuid(), "managed", "Managed", "managed@example.com", "13800000000", true,
                null, null, null, null, null, null, null, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Paged = new PagedResult<ManagedUserDto>(new List<ManagedUserDto> { Dto }, 1, 1, 10);
            ById = Dto;
            Controller = new UsersController(management, userRepository)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }

        public Dictionary<string, Exception?> Exceptions { get; } = [];
        public List<RecordedProxyCall> ServiceCalls { get; } = [];
        public ManagedUserDto Dto { get; }
        public PagedResult<ManagedUserDto> Paged { get; }
        public ManagedUserDto? ById { get; set; }
        public UsersController Controller { get; }

        public void SetActor(Guid id, string claimType, bool includeSubject = true, string? subjectValue = null)
        {
            var claims = new List<Claim>();
            if (includeSubject)
                claims.Add(new Claim(claimType, subjectValue ?? id.ToString()));
            Controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }
    }
}
