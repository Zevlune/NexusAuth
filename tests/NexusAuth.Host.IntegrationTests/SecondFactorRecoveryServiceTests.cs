using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class SecondFactorRecoveryServiceTests
{
    [Fact]
    public async Task Generation_returns_ten_high_entropy_codes_and_replaces_the_previous_set()
    {
        var harness = new Harness();

        var first = await harness.Service.GenerateCodesAsync(harness.User.Id);
        var firstHashes = first.Select(UserRecoveryCode.Hash).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(10, first.Count);
        Assert.All(first, code => Assert.Equal(40, code.Length));
        Assert.Equal(10, firstHashes.Count);
        Assert.Equal(10, harness.RecoveryCodes.Count);
        Assert.All(harness.RecoveryCodes, code => Assert.DoesNotContain(code.CodeHash, first));

        var second = await harness.Service.GenerateCodesAsync(harness.User.Id);

        Assert.Equal(10, second.Count);
        Assert.DoesNotContain(second, first.Contains);
        Assert.Equal(10, harness.RecoveryCodes.Count);
        Assert.All(harness.RecoveryCodes, code => Assert.Contains(code.CodeHash, second.Select(UserRecoveryCode.Hash)));
    }

    [Fact]
    public async Task Redemption_is_user_scoped_single_use_and_counts_invalid_codes()
    {
        var harness = new Harness();
        var codes = await harness.Service.GenerateCodesAsync(harness.User.Id);

        Assert.False(await harness.Service.RedeemAsync(Guid.NewGuid(), codes[0]));
        Assert.True(await harness.Service.RedeemAsync(harness.User.Id, codes[0]));
        Assert.False(await harness.Service.RedeemAsync(harness.User.Id, codes[0]));
        Assert.False(await harness.Service.RedeemAsync(harness.User.Id, "not-a-recovery-code"));

        Assert.Equal(2, harness.FailedLoginRegistrations);
        Assert.Single(harness.RecoveryCodes, code => code.UsedAt.HasValue);
    }

    [Fact]
    public async Task Reset_disables_second_factors_revokes_sessions_and_persists_an_audit()
    {
        var harness = new Harness();
        await harness.Service.GenerateCodesAsync(harness.User.Id);
        var passkey = WebAuthnCredential.Create(
            harness.User.Id,
            [1, 2, 3],
            [4, 5, 6],
            0,
            Guid.NewGuid(),
            null,
            isBackupEligible: false,
            isBackedUp: false);
        harness.Passkeys.Add(passkey);
        var actorId = Guid.NewGuid();
        harness.User.UpdateScimProfile(harness.User.Username, harness.User.Nickname, false);

        await harness.Service.ResetAsync(harness.User.Id, actorId, "用户丢失了验证器");

        Assert.False(harness.Totp.IsEnabled);
        Assert.NotNull(passkey.DisabledAt);
        Assert.Empty(harness.RecoveryCodes);
        Assert.Equal(1, harness.RefreshTokenRevocations);
        Assert.Equal(1, harness.SessionRevocations);
        Assert.NotNull(harness.User.TokenInvalidBefore);
        var audit = Assert.Single(harness.Audits);
        Assert.Equal(actorId, audit.ActorId);
        Assert.Equal(harness.User.Id, audit.UserId);
        Assert.Equal("用户丢失了验证器", audit.Reason);
        Assert.False(harness.User.IsActive);
    }

    private sealed class Harness
    {
        private readonly List<UserRecoveryCode> recoveryCodes = [];
        private readonly List<SecondFactorRecoveryAudit> audits = [];

        public Harness()
        {
            User = User.Create("alice", "CurrentPassword!42", "Alice");
            Totp = UserCredential.CreatePendingTotp(User.Id, "protected-secret", DateTimeOffset.UtcNow.AddMinutes(5));
            Assert.True(Totp.ConfirmTotp("protected-secret", 0, DateTimeOffset.UtcNow));

            var users = HardeningProxy.Create<IUserRepository>((method, args) => method.Name switch
            {
                nameof(IUserRepository.FindByIdAsync) when (Guid)args![0]! == User.Id =>
                    Task.FromResult<User?>(User),
                nameof(IUserRepository.BeginAuthenticationAttemptAsync) when (Guid)args![0]! == User.Id =>
                    Task.FromResult<IUserAuthenticationAttempt?>(new Attempt(User)),
                nameof(IUserRepository.RegisterFailedLoginAsync) => RegisterFailure(args!),
                nameof(IUserRepository.ResetLoginFailuresAsync) => ResetFailures(),
                nameof(IUserRepository.UpdateAsync) => Task.CompletedTask,
                _ => null,
            });
            var credentials = HardeningProxy.Create<IUserCredentialRepository>((method, args) => method.Name switch
            {
                nameof(IUserCredentialRepository.GetEnabledTotpAsync) =>
                    Task.FromResult<IReadOnlyList<UserCredential>>([Totp]),
                nameof(IUserCredentialRepository.DisableAllTotpAsync) => DisableTotp((DateTimeOffset)args![1]!),
                _ => null,
            });
            var passkeys = HardeningProxy.Create<IWebAuthnCredentialRepository>((method, args) => method.Name switch
            {
                nameof(IWebAuthnCredentialRepository.DisableAllForUserAsync) => DisablePasskeys((DateTimeOffset)args![1]!),
                _ => null,
            });
            var recovery = HardeningProxy.Create<ISecondFactorRecoveryRepository>((method, args) => method.Name switch
            {
                nameof(ISecondFactorRecoveryRepository.ExecuteInTransactionAsync) =>
                    ((Func<CancellationToken, Task>)args![0]!)((CancellationToken)args[1]!),
                nameof(ISecondFactorRecoveryRepository.ReplaceAsync) => Replace((IReadOnlyCollection<UserRecoveryCode>)args![1]!),
                nameof(ISecondFactorRecoveryRepository.TryRedeemAsync) => TryRedeem((Guid)args![0]!, (string)args[1]!, (DateTimeOffset)args[2]!),
                nameof(ISecondFactorRecoveryRepository.DeleteForUserAsync) => DeleteCodes(),
                nameof(ISecondFactorRecoveryRepository.AddAuditAsync) => AddAudit((SecondFactorRecoveryAudit)args![0]!),
                _ => null,
            });
            var refreshTokens = HardeningProxy.Create<IRefreshTokenRepository>((method, _) =>
                method.Name == nameof(IRefreshTokenRepository.RevokeAllForUserAsync)
                    ? RevokeRefreshTokens()
                    : null);
            var sessions = HardeningProxy.Create<ISsoSessionService>((method, _) =>
                method.Name == nameof(ISsoSessionService.RevokeAllForUserAsync)
                    ? RevokeSessions()
                    : null);

            Service = new SecondFactorRecoveryService(
                users,
                credentials,
                passkeys,
                recovery,
                refreshTokens,
                sessions,
                Options.Create(new NexusAuthSecurityOptions { LoginFailureLimit = 3, LoginLockoutMinutes = 5 }),
                NullLogger<SecondFactorRecoveryService>.Instance);
        }

        public User User { get; }
        public UserCredential Totp { get; }
        public List<WebAuthnCredential> Passkeys { get; } = [];
        public SecondFactorRecoveryService Service { get; }
        public IReadOnlyList<UserRecoveryCode> RecoveryCodes => recoveryCodes;
        public IReadOnlyList<SecondFactorRecoveryAudit> Audits => audits;
        public int FailedLoginRegistrations { get; private set; }
        public int RefreshTokenRevocations { get; private set; }
        public int SessionRevocations { get; private set; }

        private object? RegisterFailure(object?[] args)
        {
            FailedLoginRegistrations++;
            User.RegisterFailedLogin((DateTimeOffset)args[3]!, (int)args[1]!, (TimeSpan)args[2]!);
            return Task.FromResult<User?>(User);
        }

        private object ResetFailures()
        {
            User.RegisterSuccessfulLogin(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }

        private object DisableTotp(DateTimeOffset now)
        {
            Totp.Disable(now);
            return Task.FromResult(1);
        }

        private object DisablePasskeys(DateTimeOffset now)
        {
            foreach (var passkey in Passkeys)
                passkey.Disable(now);
            return Task.FromResult(Passkeys.Count);
        }

        private object Replace(IReadOnlyCollection<UserRecoveryCode> codes)
        {
            recoveryCodes.Clear();
            recoveryCodes.AddRange(codes);
            return Task.CompletedTask;
        }

        private object TryRedeem(Guid userId, string hash, DateTimeOffset now)
        {
            var code = recoveryCodes.FirstOrDefault(item => item.UserId == userId
                && item.CodeHash == hash
                && item.UsedAt is null);
            if (code is null)
                return Task.FromResult(false);
            code.MarkUsed(now);
            return Task.FromResult(true);
        }

        private object DeleteCodes()
        {
            recoveryCodes.Clear();
            return Task.CompletedTask;
        }

        private object AddAudit(SecondFactorRecoveryAudit audit)
        {
            audits.Add(audit);
            return Task.CompletedTask;
        }

        private object RevokeRefreshTokens()
        {
            RefreshTokenRevocations++;
            return Task.CompletedTask;
        }

        private object RevokeSessions()
        {
            SessionRevocations++;
            return Task.CompletedTask;
        }
    }

    private sealed class Attempt(User user) : IUserAuthenticationAttempt
    {
        public User User { get; } = user;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
