using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class UserManagementServiceTests
{
    [Fact]
    public async Task CreateAsync_trims_username_nickname_email_and_phone()
    {
        var harness = new Harness();
        var request = ValidCreate() with
        {
            Username = "  alice  ",
            Nickname = "  Alice Cooper  ",
            Email = "  Alice@Example.com  ",
            PhoneNumber = "  13800000000  ",
        };

        var dto = await harness.Service.CreateAsync(request, CancellationToken.None);

        var added = Assert.IsType<User>(harness.AddedUser);
        Assert.Equal("alice", added.Username);
        Assert.Equal("Alice Cooper", added.Nickname);
        Assert.Equal("alice@example.com", added.Email);
        Assert.Equal("13800000000", added.PhoneNumber);
        Assert.Equal("alice", dto.Username);
        Assert.Equal("Alice Cooper", dto.Nickname);
        Assert.Equal("alice@example.com", dto.Email);
        Assert.Equal("13800000000", dto.PhoneNumber);
        Assert.Equal("alice", harness.CallArg<string>(nameof(IUserRepository.FindByUsernameAsync), 0));
        Assert.Equal("alice@example.com", harness.CallArg<string>(nameof(IUserRepository.FindByEmailAsync), 0));
        Assert.Equal("13800000000", harness.CallArg<string>(nameof(IUserRepository.FindByPhoneNumberAsync), 0));
    }

    [Fact]
    public async Task CreateAsync_stores_a_hashed_password_and_never_the_raw_value()
    {
        var harness = new Harness();

        var dto = await harness.Service.CreateAsync(ValidCreate() with { Password = "Sup3rSecret!" }, CancellationToken.None);

        var added = Assert.IsType<User>(harness.AddedUser);
        Assert.NotEqual("Sup3rSecret!", added.PasswordHash);
        Assert.True(added.VerifyPassword("Sup3rSecret!"));
        Assert.DoesNotContain("Sup3rSecret!", added.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(ManagedUserDto).GetProperties(),
            property => property.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAsync_defaults_to_enabled_and_honors_explicit_disable()
    {
        var enabledHarness = new Harness();
        var enabled = await enabledHarness.Service.CreateAsync(ValidCreate(), CancellationToken.None);
        Assert.True(Assert.IsType<User>(enabledHarness.AddedUser).IsActive);
        Assert.True(enabled.IsActive);

        var disabledHarness = new Harness();
        var disabled = await disabledHarness.Service.CreateAsync(ValidCreate() with { IsActive = false }, CancellationToken.None);
        Assert.False(Assert.IsType<User>(disabledHarness.AddedUser).IsActive);
        Assert.False(disabled.IsActive);
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_username_email_and_phone_without_adding()
    {
        var existing = User.Create("taken", "pw-for-fixture", "Existing");

        var usernameHarness = new Harness { ExistingUsername = existing };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            usernameHarness.Service.CreateAsync(ValidCreate() with { Username = "  taken  " }, CancellationToken.None));
        Assert.Null(usernameHarness.AddedUser);
        Assert.Equal("taken", usernameHarness.CallArg<string>(nameof(IUserRepository.FindByUsernameAsync), 0));

        var emailHarness = new Harness { ExistingEmail = existing };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            emailHarness.Service.CreateAsync(ValidCreate() with { Email = "  Taken@Example.com  " }, CancellationToken.None));
        Assert.Null(emailHarness.AddedUser);
        Assert.Equal("taken@example.com", emailHarness.CallArg<string>(nameof(IUserRepository.FindByEmailAsync), 0));

        var phoneHarness = new Harness { ExistingPhone = existing };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            phoneHarness.Service.CreateAsync(ValidCreate() with { PhoneNumber = "  13800000000  " }, CancellationToken.None));
        Assert.Null(phoneHarness.AddedUser);
        Assert.Equal("13800000000", phoneHarness.CallArg<string>(nameof(IUserRepository.FindByPhoneNumberAsync), 0));
    }

    [Fact]
    public async Task CreateAsync_rejects_empty_required_fields_and_out_of_range_profile_fields()
    {
        await AssertCreateRejected(ValidCreate() with { Username = "" });
        await AssertCreateRejected(ValidCreate() with { Username = "   " });
        await AssertCreateRejected(ValidCreate() with { Password = "" });
        await AssertCreateRejected(ValidCreate() with { Password = "   " });
        await AssertCreateRejected(ValidCreate() with { Nickname = "" });
        await AssertCreateRejected(ValidCreate() with { Nickname = "   " });
        await AssertCreateRejected(ValidCreate() with { Username = new string('u', 101) });
        await AssertCreateRejected(ValidCreate() with { Nickname = new string('n', 101) });
        await AssertCreateRejected(ValidCreate() with { Email = "not-an-email" });
        await AssertCreateRejected(ValidCreate() with { Email = new string('e', 257) });
        await AssertCreateRejected(ValidCreate() with { PhoneNumber = new string('1', 21) });
        await AssertCreateRejected(ValidCreate() with { GivenName = new string('g', 101) });
        await AssertCreateRejected(ValidCreate() with { Timezone = new string('t', 101) });
    }

    [Fact]
    public async Task CreateAsync_enforces_the_72_byte_utf8_password_limit()
    {
        var overLimit = string.Concat(Enumerable.Repeat("\u6c49", 25));
        Assert.Equal(75, Encoding.UTF8.GetByteCount(overLimit));
        Assert.True(overLimit.Length <= 72);

        await AssertCreateRejected(ValidCreate() with { Password = overLimit });

        var atLimit = string.Concat(Enumerable.Repeat("\u6c49", 24));
        Assert.Equal(72, Encoding.UTF8.GetByteCount(atLimit));
        var harness = new Harness();
        var dto = await harness.Service.CreateAsync(ValidCreate() with { Password = atLimit }, CancellationToken.None);
        Assert.True(Assert.IsType<User>(harness.AddedUser).VerifyPassword(atLimit));
        Assert.Equal("alice", dto.Username);
    }

    [Fact]
    public async Task UpdateProfileAsync_rejects_phone_used_by_another_user_without_updating()
    {
        var target = User.Create("target", "pw-for-fixture", "Target");
        var other = User.Create("other", "pw-for-fixture", "Other");
        var harness = new Harness { ExistingPhone = other };
        harness.Register(target, other);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.UpdateProfileAsync(target.Id, ValidUpdate() with { PhoneNumber = "  13900000000  " }, CancellationToken.None));

        Assert.DoesNotContain(harness.Calls, call => call.Method.Name == nameof(IUserRepository.UpdateAsync));
        Assert.Equal("13900000000", harness.CallArg<string>(nameof(IUserRepository.FindByPhoneNumberAsync), 0));
    }

    [Fact]
    public async Task UpdateProfileAsync_normalizes_profile_values_on_success()
    {
        var target = User.Create("target", "pw-for-fixture", "Target");
        var harness = new Harness();
        harness.Register(target);

        var dto = await harness.Service.UpdateProfileAsync(target.Id, ValidUpdate() with
        {
            Nickname = "  Renamed  ",
            Email = "  Target@Example.com  ",
            PhoneNumber = "  13900000000  ",
        }, CancellationToken.None);

        Assert.Equal("Renamed", target.Nickname);
        Assert.Equal("target@example.com", target.Email);
        Assert.Equal("13900000000", target.PhoneNumber);
        Assert.Equal("Renamed", dto.Nickname);
        Assert.Contains(harness.Calls, call => call.Source == "users" && call.Method.Name == nameof(IUserRepository.UpdateAsync));
    }

    [Fact]
    public async Task RevokeTokensAsync_preserves_active_status_and_revokes_refresh_and_sessions()
    {
        var user = User.Create("active", "pw-for-fixture", "Active");
        var harness = new Harness();
        harness.Register(user);
        var before = DateTimeOffset.UtcNow;

        await harness.Service.RevokeTokensAsync(user.Id, CancellationToken.None);

        Assert.True(user.IsActive);
        Assert.NotNull(user.TokenInvalidBefore);
        Assert.InRange(user.TokenInvalidBefore!.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(
            new[] { "users:UpdateAsync", "refresh:RevokeAllForUserAsync", "sessions:RevokeAllForUserAsync" },
            harness.MutationOrder());
    }

    [Fact]
    public async Task UpdateStatusAsync_disable_invalidates_tokens_and_revokes_refresh_and_sessions()
    {
        var user = User.Create("active", "pw-for-fixture", "Active");
        var harness = new Harness();
        harness.Register(user);

        var dto = await harness.Service.UpdateStatusAsync(user.Id, isActive: false, CancellationToken.None);

        Assert.False(user.IsActive);
        Assert.False(dto.IsActive);
        Assert.NotNull(user.TokenInvalidBefore);
        Assert.Equal(
            new[] { "users:UpdateAsync", "refresh:RevokeAllForUserAsync", "sessions:RevokeAllForUserAsync" },
            harness.MutationOrder());
    }

    [Fact]
    public async Task ResetPasswordAsync_changes_password_and_invalidates_tokens()
    {
        var user = User.Create("active", "old-password", "Active");
        var harness = new Harness();
        harness.Register(user);

        await harness.Service.ResetPasswordAsync(user.Id, new ResetManagedUserPasswordRequest("new-password"), CancellationToken.None);

        Assert.True(user.VerifyPassword("new-password"));
        Assert.False(user.VerifyPassword("old-password"));
        Assert.NotNull(user.TokenInvalidBefore);
        Assert.Equal(
            new[] { "users:UpdateAsync", "refresh:RevokeAllForUserAsync", "sessions:RevokeAllForUserAsync" },
            harness.MutationOrder());
    }

    [Fact]
    public async Task DeleteAsync_disables_and_cuts_off_tokens_before_revoking_and_deleting()
    {
        var user = User.Create("active", "pw-for-fixture", "Active");
        var harness = new Harness();
        harness.Register(user);

        await harness.Service.DeleteAsync(user.Id, CancellationToken.None);

        Assert.False(user.IsActive);
        Assert.NotNull(user.TokenInvalidBefore);
        Assert.Equal(
            new[] { "users:UpdateAsync", "refresh:RevokeAllForUserAsync", "sessions:RevokeAllForUserAsync", "users:DeleteAsync" },
            harness.MutationOrder());
        Assert.Contains(harness.Calls, call => call.Source == "users" && call.Method.Name == nameof(IUserRepository.DeleteAsync));
    }

    [Theory]
    [InlineData(nameof(IUserManagementService.UpdateProfileAsync))]
    [InlineData(nameof(IUserManagementService.UpdateStatusAsync))]
    [InlineData(nameof(IUserManagementService.ResetPasswordAsync))]
    [InlineData(nameof(IUserManagementService.DeleteAsync))]
    [InlineData(nameof(IUserManagementService.RevokeTokensAsync))]
    public async Task Protected_system_users_are_never_mutated(string operation)
    {
        var system = User.Create("system", "pw-for-fixture", "System");
        system.MarkAsSystemAccount();
        var hashBefore = system.PasswordHash;
        var harness = new Harness();
        harness.Register(system);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(harness.Service, operation, system.Id));

        Assert.Empty(harness.MutationOrder());
        Assert.Equal(hashBefore, system.PasswordHash);
        Assert.True(system.IsActive);
        Assert.Null(system.TokenInvalidBefore);
    }

    [Theory]
    [InlineData(nameof(IUserManagementService.UpdateProfileAsync))]
    [InlineData(nameof(IUserManagementService.UpdateStatusAsync))]
    [InlineData(nameof(IUserManagementService.ResetPasswordAsync))]
    [InlineData(nameof(IUserManagementService.DeleteAsync))]
    [InlineData(nameof(IUserManagementService.RevokeTokensAsync))]
    public async Task Missing_users_are_reported_and_never_mutated(string operation)
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => InvokeAsync(harness.Service, operation, Guid.NewGuid()));

        Assert.Empty(harness.MutationOrder());
    }

    private static Task InvokeAsync(UserManagementService service, string operation, Guid id) => operation switch
    {
        nameof(IUserManagementService.UpdateProfileAsync) => service.UpdateProfileAsync(id, ValidUpdate(), CancellationToken.None),
        nameof(IUserManagementService.UpdateStatusAsync) => service.UpdateStatusAsync(id, isActive: false, CancellationToken.None),
        nameof(IUserManagementService.ResetPasswordAsync) => service.ResetPasswordAsync(id, new ResetManagedUserPasswordRequest("new-password"), CancellationToken.None),
        nameof(IUserManagementService.DeleteAsync) => service.DeleteAsync(id, CancellationToken.None),
        nameof(IUserManagementService.RevokeTokensAsync) => service.RevokeTokensAsync(id, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown user-management operation."),
    };

    private static async Task AssertCreateRejected(CreateManagedUserRequest request)
    {
        var harness = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.CreateAsync(request, CancellationToken.None));
        Assert.Null(harness.AddedUser);
        Assert.Empty(harness.Calls);
    }

    private static CreateManagedUserRequest ValidCreate() =>
        new("alice", "Sup3rSecret!", "Alice");

    private static UpdateManagedUserRequest ValidUpdate() =>
        new("Updated", null, null, null, null, null, null, null, null, null);

    private sealed class Harness
    {
        private readonly Dictionary<Guid, User> usersById = [];

        public Harness()
        {
            UserRepository = RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
            {
                Calls.Add(new RecordedProxyCall("users", method, args));
                switch (method.Name)
                {
                    case nameof(IUserRepository.FindByUsernameAsync):
                        return ExistingUsername;
                    case nameof(IUserRepository.FindByEmailAsync):
                        return ExistingEmail;
                    case nameof(IUserRepository.FindByPhoneNumberAsync):
                        return ExistingPhone;
                    case nameof(IUserRepository.FindByIdAsync):
                        return args![0] is Guid id ? usersById.GetValueOrDefault(id) : null;
                    case nameof(IUserRepository.AddAsync):
                        AddedUser = (User)args![0]!;
                        return null;
                    default:
                        return null;
                }
            });
            RefreshTokenRepository = RecordingDispatchProxy.Create<IRefreshTokenRepository>((method, args) =>
            {
                Calls.Add(new RecordedProxyCall("refresh", method, args));
                return null;
            });
            SessionService = RecordingDispatchProxy.Create<ISsoSessionService>((method, args) =>
            {
                Calls.Add(new RecordedProxyCall("sessions", method, args));
                return null;
            });
            Service = new UserManagementService(
                UserRepository, RefreshTokenRepository, SessionService, NullLogger<UserManagementService>.Instance);
        }

        public List<RecordedProxyCall> Calls { get; } = [];
        public User? ExistingUsername { get; init; }
        public User? ExistingEmail { get; init; }
        public User? ExistingPhone { get; init; }
        public User? AddedUser { get; private set; }
        public IUserRepository UserRepository { get; }
        public IRefreshTokenRepository RefreshTokenRepository { get; }
        public ISsoSessionService SessionService { get; }
        public UserManagementService Service { get; }

        public void Register(params User[] users)
        {
            foreach (var user in users)
                usersById[user.Id] = user;
        }

        public T? CallArg<T>(string methodName, int index)
        {
            var call = Assert.Single(Calls, candidate => candidate.Method.Name == methodName);
            return (T?)call.Arguments![index];
        }

        public string[] MutationOrder() => Calls
            .Where(call => call.Method.Name is
                nameof(IUserRepository.AddAsync) or
                nameof(IUserRepository.UpdateAsync) or
                nameof(IUserRepository.DeleteAsync) or
                nameof(IRefreshTokenRepository.RevokeAllForUserAsync) or
                nameof(ISsoSessionService.RevokeAllForUserAsync))
            .Select(call => $"{call.Source}:{call.Method.Name}")
            .ToArray();
    }
}

internal sealed record RecordedProxyCall(string Source, MethodInfo Method, object?[]? Arguments);

public class RecordingDispatchProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> handler = null!;

    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, RecordingDispatchProxy>();
        ((RecordingDispatchProxy)(object)proxy).handler = handler;
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
