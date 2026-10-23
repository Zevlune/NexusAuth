using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexusAuth.Application;
using NexusAuth.Application.Users;
using NexusAuth.Workbench.Api.Controllers;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class UsersControllerTests
{
    [Fact]
    public async Task All_actions_return_service_results_without_cache()
    {
        var harness = new Harness();
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
        var harness = new Harness { ById = null };

        var result = await harness.Controller.GetById(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal("no-store", harness.Controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Service_exceptions_map_to_bad_request_not_found_and_conflict()
    {
        var harness = new Harness();
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
        var harness = new Harness();

        var result = await harness.Controller.Create(ValidCreate(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(ManagedUserDto).GetProperties(),
            property => property.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    private static string? MessageOf(object? value) =>
        value?.GetType().GetProperty("message")?.GetValue(value) as string;

    private static CreateManagedUserRequest ValidCreate() =>
        new("managed", "Sup3rSecret!", "Managed");

    private static UpdateManagedUserRequest ValidUpdate() =>
        new("Updated", null, null, null, null, null, null, null, null, null);

    private sealed class Harness
    {
        public Harness()
        {
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
            Controller = new UsersController(management)
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
    }
}
