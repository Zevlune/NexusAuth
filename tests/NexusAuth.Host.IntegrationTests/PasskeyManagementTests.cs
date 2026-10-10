using System.Reflection;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class PasskeyManagementTests
{
    [Fact]
    public async Task Get_returns_only_safe_passkey_metadata()
    {
        var userId = Guid.NewGuid();
        var credential = WebAuthnCredential.Create(
            userId,
            [1, 2, 3],
            [4, 5, 6],
            42,
            Guid.NewGuid(),
            ["internal"],
            isBackupEligible: true,
            isBackedUp: true,
            "Laptop",
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        credential.RecordAuthentication(43, isBackedUp: true, DateTimeOffset.Parse("2026-10-08T00:00:00Z"));
        credential.Disable(DateTimeOffset.Parse("2026-10-09T00:00:00Z"));
        var harness = new Harness { Credentials = [credential] };

        var result = await harness.Service.GetAsync(userId);

        var summary = Assert.Single(result);
        Assert.Equal(credential.Id, summary.Id);
        Assert.Equal("Laptop", summary.DisplayName);
        Assert.Equal(credential.CreatedAt, summary.CreatedAt);
        Assert.Equal(credential.LastUsedAt, summary.LastUsedAt);
        Assert.Equal(credential.DisabledAt, summary.DisabledAt);
        Assert.True(summary.IsBackupEligible);
        Assert.True(summary.IsBackedUp);
        Assert.Equal(
            new[] { "CreatedAt", "DisabledAt", "DisplayName", "Id", "IsBackedUp", "IsBackupEligible", "LastUsedAt" },
            summary.GetType().GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_rejects_empty_names_without_repository_calls(string name)
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.RenameAsync(Guid.NewGuid(), Guid.NewGuid(), name));

        Assert.Empty(harness.CredentialCalls);
    }

    [Fact]
    public async Task Rename_rejects_names_over_128_characters()
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.RenameAsync(Guid.NewGuid(), Guid.NewGuid(), new string('x', 129)));

        Assert.Empty(harness.CredentialCalls);
    }

    [Fact]
    public async Task Rename_trims_name_and_does_not_change_credentials_owned_by_another_user()
    {
        var credentialOwnerId = Guid.NewGuid();
        var requestingUserId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var harness = new Harness
        {
            CredentialCall = (method, args) => method.Name == nameof(IWebAuthnCredentialRepository.RenameAsync)
                && (Guid)args![0]! == credentialOwnerId
                && (Guid)args[1]! == credentialId,
        };

        Assert.False(await harness.Service.RenameAsync(requestingUserId, credentialId, "  Work laptop  "));
        Assert.Equal(requestingUserId, harness.CredentialCalls.Single().Arguments![0]);
        Assert.Equal("Work laptop", harness.CredentialCalls.Single().Arguments![2]);
        Assert.DoesNotContain(harness.CredentialCalls, call => call.Method.Name == nameof(IWebAuthnCredentialRepository.UpdateAsync));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Revoke_rejects_empty_reasons_without_revoking(string reason)
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.RevokeAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reason));

        Assert.Empty(harness.CredentialCalls);
        Assert.Empty(harness.UserCalls);
        Assert.Empty(harness.TokenCalls);
        Assert.Empty(harness.SessionCalls);
    }

    [Fact]
    public async Task Revoke_rejects_long_or_control_character_reasons()
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.RevokeAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('x', 501)));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.RevokeAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "security\nincident"));

        Assert.Empty(harness.CredentialCalls);
        Assert.Empty(harness.UserCalls);
        Assert.Empty(harness.TokenCalls);
        Assert.Empty(harness.SessionCalls);
    }

    [Fact]
    public async Task Revoke_does_not_change_or_revoke_a_credential_owned_by_another_user()
    {
        var credentialOwnerId = Guid.NewGuid();
        var requestingUserId = Guid.NewGuid();
        var harness = new Harness
        {
            User = User.Create("alice", "test-password", "Alice"),
            CredentialCall = (method, args) => method.Name == nameof(IWebAuthnCredentialRepository.DisableAsync)
                && (Guid)args![0]! == credentialOwnerId,
        };

        Assert.False(await harness.Service.RevokeAsync(requestingUserId, Guid.NewGuid(), Guid.NewGuid(), "Reported lost"));

        Assert.Equal(requestingUserId, harness.CredentialCalls.Single().Arguments![0]);
        Assert.DoesNotContain(harness.UserCalls, call => call.Method.Name == nameof(IUserRepository.UpdateAsync));
        Assert.Empty(harness.TokenCalls);
        Assert.Empty(harness.SessionCalls);
    }

    [Fact]
    public async Task Revoke_of_missing_user_or_missing_or_already_disabled_credential_does_not_revoke_tokens_or_sessions()
    {
        var existingUser = User.Create("alice", "test-password", "Alice");
        var harness = new Harness
        {
            User = existingUser,
            CredentialCall = (_, _) => false,
        };
        var userId = existingUser.Id;

        harness.User = null;
        Assert.False(await harness.Service.RevokeAsync(userId, Guid.NewGuid(), Guid.NewGuid(), "Reported lost"));
        Assert.Empty(harness.CredentialCalls);

        harness.User = existingUser;
        Assert.False(await harness.Service.RevokeAsync(userId, Guid.NewGuid(), Guid.NewGuid(), "Reported lost"));

        Assert.DoesNotContain(harness.UserCalls, call => call.Method.Name == nameof(IUserRepository.UpdateAsync));
        Assert.Empty(harness.TokenCalls);
        Assert.Empty(harness.SessionCalls);
    }

    [Fact]
    public async Task Successful_revoke_sets_token_cutoff_and_revokes_all_tokens_and_sessions()
    {
        var user = User.Create("alice", "test-password", "Alice");
        var userId = user.Id;
        var credentialId = Guid.NewGuid();
        var harness = new Harness
        {
            User = user,
            CredentialCall = (method, args) => method.Name == nameof(IWebAuthnCredentialRepository.DisableAsync)
                && (Guid)args![0]! == userId
                && (Guid)args[1]! == credentialId,
        };
        var startedAt = DateTimeOffset.UtcNow;

        Assert.True(await harness.Service.RevokeAsync(userId, credentialId, Guid.NewGuid(), "  Reported lost  "));

        Assert.NotNull(user.TokenInvalidBefore);
        Assert.InRange(user.TokenInvalidBefore.Value, startedAt.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        var updateCall = Assert.Single(harness.UserCalls, call => call.Method.Name == nameof(IUserRepository.UpdateAsync));
        Assert.Same(user, Assert.IsType<User>(updateCall.Arguments![0]));
        Assert.Equal(userId, Assert.Single(harness.TokenCalls).Arguments![0]);
        Assert.Equal(userId, Assert.Single(harness.SessionCalls).Arguments![0]);
        Assert.Equal(1, harness.CredentialCalls.Count(call => call.Method.Name == nameof(IWebAuthnCredentialRepository.DisableAsync)));
    }

    private sealed class Harness
    {
        public User? User { get; set; }
        public IReadOnlyList<WebAuthnCredential> Credentials { get; set; } = [];
        public Func<MethodInfo, object?[]?, bool>? CredentialCall { get; init; }
        public List<RecordedCall> CredentialCalls { get; } = [];
        public List<RecordedCall> UserCalls { get; } = [];
        public List<RecordedCall> TokenCalls { get; } = [];
        public List<RecordedCall> SessionCalls { get; } = [];

        private readonly IWebAuthnCredentialRepository credentials;
        private readonly IUserRepository users;
        private readonly IRefreshTokenRepository tokens;
        private readonly ISsoSessionService sessions;

        public Harness()
        {
            credentials = RecordingProxy.Create<IWebAuthnCredentialRepository>((method, args) =>
            {
                CredentialCalls.Add(new RecordedCall(method, args));
                return method.Name switch
                {
                    nameof(IWebAuthnCredentialRepository.GetForUserAsync) => Credentials,
                    nameof(IWebAuthnCredentialRepository.RenameAsync) or nameof(IWebAuthnCredentialRepository.DisableAsync) =>
                        CredentialCall?.Invoke(method, args) ?? true,
                    _ => null,
                };
            });
            users = RecordingProxy.Create<IUserRepository>((method, args) =>
            {
                UserCalls.Add(new RecordedCall(method, args));
                return method.Name == nameof(IUserRepository.FindByIdAsync) ? User : null;
            });
            tokens = RecordingProxy.Create<IRefreshTokenRepository>((method, args) =>
            {
                TokenCalls.Add(new RecordedCall(method, args));
                return null;
            });
            sessions = RecordingProxy.Create<ISsoSessionService>((method, args) =>
            {
                SessionCalls.Add(new RecordedCall(method, args));
                return null;
            });
        }

        public PasskeyManagementService Service => new(
            credentials,
            users,
            tokens,
            sessions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PasskeyManagementService>.Instance);
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
