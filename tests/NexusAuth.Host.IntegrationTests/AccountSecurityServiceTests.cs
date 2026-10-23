using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Email;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Shared.Email;
using Xunit;
using Proxy = NexusAuth.Host.IntegrationTests.PasskeyManagementTests.RecordingProxy;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// Service-level regression tests for account lockout, password policy and
/// enumeration-resistant error messages.
/// </summary>
public sealed class AccountSecurityServiceTests
{
    private const string CurrentPassword = "CurrentPassword!42";

    [Fact]
    public async Task Invalid_totp_codes_count_toward_the_persistent_login_lockout()
    {
        var harness = new TotpHarness(failureLimit: 3, lockoutMinutes: 7);

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.False(await harness.Service.ValidateAsync(harness.User.Id, harness.WrongCode()));

        Assert.Equal(3, harness.FailedLoginRegistrations.Count);
        Assert.All(harness.FailedLoginRegistrations, registration =>
        {
            Assert.Equal(3, registration.Limit);
            Assert.Equal(TimeSpan.FromMinutes(7), registration.Duration);
        });
        Assert.True(harness.User.IsLoginLocked(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Locked_accounts_reject_even_a_valid_totp_code_without_consuming_it()
    {
        var harness = new TotpHarness();
        harness.User.RegisterFailedLogin(DateTimeOffset.UtcNow, 1, TimeSpan.FromMinutes(5));

        Assert.False(await harness.Service.ValidateAsync(harness.User.Id, harness.CurrentCode()));

        Assert.Equal(0, harness.CounterUses);
        Assert.Empty(harness.FailedLoginRegistrations);
    }

    [Fact]
    public async Task Valid_totp_code_is_accepted_without_registering_a_failure()
    {
        var harness = new TotpHarness();

        Assert.True(await harness.Service.ValidateAsync(harness.User.Id, harness.CurrentCode()));

        Assert.Equal(1, harness.CounterUses);
        Assert.Empty(harness.FailedLoginRegistrations);
    }

    [Fact]
    public async Task Pending_enrollment_is_rebuilt_from_server_state_only()
    {
        var harness = new TotpHarness();
        var enrollment = await harness.Service.BeginEnrollmentAsync(harness.User.Id);

        var pending = await harness.Service.FindPendingEnrollmentAsync(harness.User.Id, enrollment.ProtectedSecret);

        Assert.NotNull(pending);
        Assert.Equal(enrollment.ManualEntryKey, pending!.ManualEntryKey);
        Assert.StartsWith("otpauth://totp/", pending.OtpauthUri);
        Assert.Equal(enrollment.OtpauthUri, pending.OtpauthUri);
        Assert.Null(await harness.Service.FindPendingEnrollmentAsync(harness.User.Id, "p:unknown"));
        Assert.Null(await harness.Service.FindPendingEnrollmentAsync(Guid.NewGuid(), enrollment.ProtectedSecret));
    }

    [Fact]
    public async Task Successful_password_check_does_not_reset_failures_before_the_login_completes()
    {
        var harness = new UserServiceHarness();

        Assert.Same(harness.User, await harness.Service.ValidateCredentialsAsync("alice", CurrentPassword));

        Assert.DoesNotContain(nameof(IUserRepository.ResetLoginFailuresAsync), harness.Calls);
    }

    [Theory]
    [InlineData("a", PasswordPolicy.TooShortMessage)]
    [InlineData("Short1!", PasswordPolicy.TooShortMessage)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", PasswordPolicy.TooLongMessage)]
    public async Task Change_password_enforces_the_shared_password_policy(string newPassword, string expectedMessage)
    {
        var harness = new UserServiceHarness();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.ChangePasswordAsync(harness.User.Id, CurrentPassword, newPassword));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Empty(harness.Calls);
        Assert.True(harness.User.VerifyPassword(CurrentPassword));
    }

    [Fact]
    public async Task Change_password_with_wrong_current_password_counts_toward_lockout()
    {
        var harness = new UserServiceHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.ChangePasswordAsync(harness.User.Id, "WrongPassword!42", "NewPassword!42"));

        Assert.Contains(nameof(IUserRepository.RegisterFailedLoginAsync), harness.Calls);
        Assert.DoesNotContain(nameof(IUserRepository.UpdateAsync), harness.Calls);
        Assert.True(harness.User.VerifyPassword(CurrentPassword));
    }

    [Fact]
    public async Task Change_password_is_rejected_while_the_account_is_locked()
    {
        var harness = new UserServiceHarness();
        harness.User.RegisterFailedLogin(DateTimeOffset.UtcNow, 1, TimeSpan.FromMinutes(5));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.ChangePasswordAsync(harness.User.Id, CurrentPassword, "NewPassword!42"));

        Assert.Equal(UserService.AccountLockedMessage, exception.Message);
        Assert.DoesNotContain(nameof(IUserRepository.UpdateAsync), harness.Calls);
        Assert.True(harness.User.VerifyPassword(CurrentPassword));
    }

    [Fact]
    public async Task Registration_does_not_echo_conflicting_email_or_phone()
    {
        var harness = new UserServiceHarness();

        var emailConflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.RegisterAsync("new-user", "NewPassword!42", "New", email: "user@example.test"));
        var phoneConflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.RegisterAsync("new-user", "NewPassword!42", "New", phoneNumber: "13800000000"));

        Assert.Equal(UserService.ContactUnavailableMessage, emailConflict.Message);
        Assert.Equal(UserService.ContactUnavailableMessage, phoneConflict.Message);
        Assert.DoesNotContain("user@example.test", emailConflict.Message);
        Assert.DoesNotContain("13800000000", phoneConflict.Message);
        Assert.DoesNotContain(nameof(IUserRepository.AddAsync), harness.Calls);
    }

    [Fact]
    public async Task Registration_respects_the_user_whitelist_and_password_policy()
    {
        var harness = new UserServiceHarness(new NexusAuthSecurityOptions { UserWhitelist = ["alice"] });

        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.RegisterAsync("mallory", "NewPassword!42", "Mallory", email: "mallory@example.test"));
        var weak = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.RegisterAsync("alice2", "short", "Alice", email: "alice2@example.test"));

        Assert.Equal(UserService.RegistrationDeniedMessage, denied.Message);
        Assert.Equal(PasswordPolicy.TooShortMessage, weak.Message);
        Assert.DoesNotContain(nameof(IUserRepository.AddAsync), harness.Calls);
    }

    [Fact]
    public async Task Password_reset_returns_one_generic_message_for_every_failure_mode()
    {
        var unknown = new ResetHarness(userExists: false);
        var unknownError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unknown.Service.ResetPasswordAsync("user@example.test", "123456", "NewPassword!42", null, default));

        var wrongCode = new ResetHarness(totpEnabled: true);
        await wrongCode.Service.SendPublicAsync("user@example.test", "reset-password", default);
        var wrongCodeError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wrongCode.Service.ResetPasswordAsync("user@example.test", wrongCode.OtherCode(), "NewPassword!42", "123456", default));

        var wrongTotp = new ResetHarness(totpEnabled: true);
        await wrongTotp.Service.SendPublicAsync("user@example.test", "reset-password", default);
        var wrongTotpError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wrongTotp.Service.ResetPasswordAsync("user@example.test", wrongTotp.LastCode, "NewPassword!42", "000000", default));

        var noTotp = new ResetHarness();
        await noTotp.Service.SendPublicAsync("user@example.test", "reset-password", default);
        var noTotpError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            noTotp.Service.ResetPasswordAsync("user@example.test", noTotp.OtherCode(), "NewPassword!42", null, default));

        Assert.All(new[] { unknownError, wrongCodeError, wrongTotpError, noTotpError },
            exception => Assert.Equal(EmailAccountService.ResetFailedMessage, exception.Message));
    }

    [Fact]
    public async Task Password_reset_verifies_the_email_code_before_offering_a_totp_oracle()
    {
        var harness = new ResetHarness(totpEnabled: true);
        await harness.Service.SendPublicAsync("user@example.test", "reset-password", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.ResetPasswordAsync("user@example.test", harness.OtherCode(), "NewPassword!42", "123456", default));
        Assert.Equal(0, harness.TotpValidations);

        // A wrong TOTP code does not burn the email code, so the legitimate user can retry.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.ResetPasswordAsync("user@example.test", harness.LastCode, "NewPassword!42", "000000", default));
        Assert.Equal(1, harness.TotpValidations);

        harness.TotpValid = true;
        await harness.Service.ResetPasswordAsync("user@example.test", harness.LastCode, "NewPassword!42", "123456", default);
        Assert.True(harness.User.VerifyPassword("NewPassword!42"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Public_email_handlers_reject_missing_input_with_validation_errors(string? value)
    {
        var harness = new ResetHarness();

        var send = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.SendPublicAsync(value!, "reset-password", default));
        var resetMissingEmail = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.ResetPasswordAsync(value!, "123456", "NewPassword!42", null, default));
        var resetMissingPassword = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.ResetPasswordAsync("user@example.test", "123456", value!, null, default));

        Assert.Equal("请输入邮箱地址。", send.Message);
        Assert.Equal("请输入邮箱地址。", resetMissingEmail.Message);
        Assert.Equal(PasswordPolicy.RequiredMessage, resetMissingPassword.Message);
        Assert.Null(await harness.Service.AuthenticateAsync(value!, "123456", default));
        Assert.Empty(harness.Sender.Messages);
    }

    [Fact]
    public async Task Password_reset_rejects_passwords_longer_than_72_utf8_bytes()
    {
        var harness = new ResetHarness();
        await harness.Service.SendPublicAsync("user@example.test", "reset-password", default);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.ResetPasswordAsync(
            "user@example.test", harness.LastCode, string.Concat(Enumerable.Repeat("汉", 25)), null, default));

        Assert.Equal(PasswordPolicy.TooLongMessage, exception.Message);
        Assert.True(harness.User.VerifyPassword(CurrentPassword));
    }

    private sealed class TotpHarness
    {
        private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        private readonly List<UserCredential> pending = [];

        public TotpHarness(int failureLimit = 5, int lockoutMinutes = 5)
        {
            Secret = TotpAlgorithm.Base32Encode(RandomNumberGenerator.GetBytes(20));
            Credential = UserCredential.CreatePendingTotp(User.Id, "p:" + Secret, createdAt.AddMinutes(10), now: createdAt);
            Assert.True(Credential.ConfirmTotp("p:" + Secret, 0, createdAt));

            var users = Proxy.Create<IUserRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IUserRepository.FindByIdAsync):
                        return (Guid)args![0]! == User.Id ? User : null;
                    case nameof(IUserRepository.RegisterFailedLoginAsync):
                        var limit = (int)args![1]!;
                        var duration = (TimeSpan)args[2]!;
                        FailedLoginRegistrations.Add((limit, duration));
                        User.RegisterFailedLogin((DateTimeOffset)args[3]!, limit, duration);
                        return User;
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
            var credentials = Proxy.Create<IUserCredentialRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IUserCredentialRepository.GetEnabledTotpAsync):
                        return (IReadOnlyList<UserCredential>)[Credential];
                    case nameof(IUserCredentialRepository.TryUseTotpCounterAsync):
                        CounterUses++;
                        return Credential.TryUseTotpCounter((long)args![2]!, (DateTimeOffset)args[3]!);
                    case nameof(IUserCredentialRepository.AddAsync):
                        pending.Add((UserCredential)args![0]!);
                        return null;
                    case nameof(IUserCredentialRepository.FindPendingTotpAsync):
                        return pending.FirstOrDefault(item => item.UserId == (Guid)args![0]!
                            && item.PendingSecretProtected == (string)args[1]!);
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });

            Service = new TotpService(
                users,
                credentials,
                new PrefixSecretProtector(),
                Options.Create(new TotpOptions()),
                Options.Create(new NexusAuthSecurityOptions
                {
                    LoginFailureLimit = failureLimit,
                    LoginLockoutMinutes = lockoutMinutes,
                }));
        }

        public User User { get; } = User.Create("alice", CurrentPassword, "Alice");
        public string Secret { get; }
        public UserCredential Credential { get; }
        public TotpService Service { get; }
        public int CounterUses { get; private set; }
        public List<(int Limit, TimeSpan Duration)> FailedLoginRegistrations { get; } = [];

        public string CurrentCode() =>
            TotpAlgorithm.ComputeCode(Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

        public string WrongCode()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            var accepted = Enumerable.Range(-1, 3).Select(offset => TotpAlgorithm.ComputeCode(Secret, now + offset)).ToHashSet();
            return Enumerable.Range(0, 1000).Select(value => value.ToString("D6")).First(code => !accepted.Contains(code));
        }
    }

    private sealed class PrefixSecretProtector : ITotpSecretProtector
    {
        public string Protect(string plaintext) => "p:" + plaintext;

        public string Unprotect(string protectedValue) => protectedValue.StartsWith("p:", StringComparison.Ordinal)
            ? protectedValue[2..]
            : throw new CryptographicException("Invalid payload.");
    }

    private sealed class UserServiceHarness
    {
        public UserServiceHarness(NexusAuthSecurityOptions? options = null)
        {
            User = User.Create("alice", CurrentPassword, "Alice", "user@example.test", "13800000000");
            var securityOptions = Options.Create(options ?? new NexusAuthSecurityOptions());
            var users = Proxy.Create<IUserRepository>((method, args) =>
            {
                Calls.Add(method.Name);
                return method.Name switch
                {
                    nameof(IUserRepository.FindByIdAsync) => (Guid)args![0]! == User.Id ? User : null,
                    nameof(IUserRepository.FindByUsernameAsync) => (string)args![0]! == User.Username ? User : null,
                    nameof(IUserRepository.FindByEmailAsync) => string.Equals((string?)args![0], User.Email, StringComparison.OrdinalIgnoreCase) ? User : null,
                    nameof(IUserRepository.FindByPhoneNumberAsync) => (string?)args![0] == User.PhoneNumber ? User : null,
                    nameof(IUserRepository.RegisterFailedLoginAsync) => User,
                    _ => null,
                };
            });
            Service = new UserService(
                users,
                Proxy.Create<ITokenService>((_, _) => null),
                Proxy.Create<ISsoSessionService>((_, _) => null),
                new SecurityPolicyService(securityOptions),
                securityOptions,
                NullLogger<UserService>.Instance);
        }

        public User User { get; }
        public List<string> Calls { get; } = [];
        public UserService Service { get; }
    }

    private sealed class ResetHarness
    {
        private static readonly Regex CodeRegex = new(@"\b(\d{6})\b", RegexOptions.Compiled);

        public ResetHarness(bool userExists = true, bool totpEnabled = false)
        {
            User = User.Create("alice", CurrentPassword, "Alice", "user@example.test");
            var users = Proxy.Create<IUserRepository>((method, args) => method.Name switch
            {
                nameof(IUserRepository.FindByIdAsync) => (Guid)args![0]! == User.Id ? User : null,
                nameof(IUserRepository.FindByEmailAsync) => userExists
                    && string.Equals((string?)args![0], User.Email, StringComparison.OrdinalIgnoreCase) ? User : null,
                _ => null,
            });
            var totp = Proxy.Create<ITotpService>((method, _) =>
            {
                if (method.Name == nameof(ITotpService.IsEnabledAsync)) return totpEnabled;
                if (method.Name == nameof(ITotpService.ValidateAsync)) { TotpValidations++; return TotpValid; }
                throw new NotSupportedException(method.Name);
            });
            var services = new ServiceCollection();
            services.AddMemoryCache();
            services.AddSingleton<IEmailSender>(Sender);
            var provider = services.BuildServiceProvider();
            var codes = new EmailVerificationCodeService(
                provider.GetRequiredService<IMemoryCache>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new SmtpOptions { Enabled = true }),
                TimeProvider.System);
            Service = new EmailAccountService(
                users,
                Proxy.Create<IUserService>((_, _) => null),
                codes,
                new SecurityPolicyService(Options.Create(new NexusAuthSecurityOptions())),
                totp,
                Proxy.Create<ITokenService>((_, _) => null),
                Proxy.Create<ISsoSessionService>((_, _) => null),
                NullLogger<EmailAccountService>.Instance);
        }

        public User User { get; }
        public RecordingSender Sender { get; } = new();
        public EmailAccountService Service { get; }
        public bool TotpValid { get; set; }
        public int TotpValidations { get; private set; }
        public string LastCode => CodeRegex.Match(Sender.Messages[^1]).Groups[1].Value;
        public string OtherCode() => LastCode == "000000" ? "000001" : "000000";
    }

    private sealed class RecordingSender : IEmailSender
    {
        public List<string> Messages { get; } = [];

        public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
        {
            Messages.Add(textBody);
            return Task.CompletedTask;
        }
    }
}
