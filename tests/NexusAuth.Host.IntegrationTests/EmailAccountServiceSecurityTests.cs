using NexusAuth.Shared.Email;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Application.Services.Email;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class EmailAccountServiceSecurityTests
{
    [Fact]
    public async Task Verification_send_requires_password_and_enabled_totp()
    {
        var fixture = new Fixture(totpEnabled: true, totpValid: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendVerificationAsync(
            fixture.User.Id, "new@example.test", Fixture.CurrentPassword, "123456", default));
        Assert.Empty(fixture.Sender.Messages);

        fixture.TotpValid = true;
        await fixture.Service.SendVerificationAsync(
            fixture.User.Id, "new@example.test", Fixture.CurrentPassword, "123456", default);
        Assert.Single(fixture.Sender.Messages);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendVerificationAsync(
            fixture.User.Id, "other@example.test", "wrong-password", "123456", default));
        Assert.Single(fixture.Sender.Messages);
    }

    [Fact]
    public async Task Verification_send_with_wrong_password_returns_friendly_error_without_sending()
    {
        var fixture = new Fixture(totpEnabled: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SendVerificationAsync(
                fixture.User.Id, "new@example.test", "WrongPassword!42", null, default));

        Assert.Equal("当前密码无效或账号不可用。", exception.Message);
        Assert.Empty(fixture.Sender.Messages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Verification_send_rejects_empty_current_password_without_sending(string? password)
    {
        var fixture = new Fixture();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SendVerificationAsync(
                fixture.User.Id, "new@example.test", password!, null, default));

        Assert.Equal("请输入当前密码。", exception.Message);
        Assert.Empty(fixture.Sender.Messages);
    }

    [Fact]
    public async Task Accounts_without_email_or_disabled_accounts_do_not_receive_public_codes_or_authenticate()
    {
        foreach (var fixture in new[]
        {
            new Fixture(hasEmail: false),
            new Fixture(active: false),
            new Fixture(userExists: false)
        })
        {
            await fixture.Service.SendPublicAsync("user@example.test", "login", default);
            await fixture.Service.SendPublicAsync("user@example.test", "reset-password", default);
            Assert.Empty(fixture.Sender.Messages);
            Assert.Null(await fixture.Service.AuthenticateAsync("user@example.test", "123456", default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ResetPasswordAsync(
                "user@example.test", "123456", "NewPassword!42", null, default));
        }
    }

    [Fact]
    public async Task Login_code_cannot_reset_password_and_reset_code_cannot_authenticate()
    {
        var fixture = new Fixture();
        await fixture.Service.SendPublicAsync("user@example.test", "login", default);
        var loginCode = fixture.Sender.LastCode;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ResetPasswordAsync(
            "user@example.test", loginCode, "NewPassword!42", null, default));
        Assert.Same(fixture.User, await fixture.Service.AuthenticateAsync("user@example.test", loginCode, default));

        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.Service.SendPublicAsync("user@example.test", "reset-password", default);
        var resetCode = fixture.Sender.LastCode;
        Assert.Null(await fixture.Service.AuthenticateAsync("user@example.test", resetCode, default));

        await fixture.Service.ResetPasswordAsync("user@example.test", resetCode, "NewPassword!42", null, default);
        Assert.True(fixture.User.VerifyPassword("NewPassword!42"));
        Assert.Equal(1, fixture.TokenRevocations);
        Assert.Equal(1, fixture.SessionRevocations);
    }

    [Fact]
    public async Task Email_binding_updates_address_and_revokes_tokens_and_sessions()
    {
        var fixture = new Fixture(hasEmail: false);
        await fixture.Service.SendVerificationAsync(
            fixture.User.Id, "new@example.test", Fixture.CurrentPassword, null, default);
        var code = fixture.Sender.LastCode;

        await fixture.Service.VerifyEmailAsync(fixture.User.Id, "new@example.test", code, default);

        Assert.Equal("new@example.test", fixture.User.Email);
        Assert.Equal(1, fixture.UserUpdates);
        Assert.Equal(1, fixture.TokenRevocations);
        Assert.Equal(1, fixture.SessionRevocations);
    }

    [Fact]
    public async Task Email_unbinding_with_current_password_clears_address_and_revokes_tokens_and_sessions()
    {
        var fixture = new Fixture();

        await fixture.Service.UnbindEmailAsync(fixture.User.Id, Fixture.CurrentPassword, null, default);

        Assert.Null(fixture.User.Email);
        Assert.Equal(1, fixture.UserUpdates);
        Assert.Equal(1, fixture.TokenRevocations);
        Assert.Equal(1, fixture.SessionRevocations);
    }

    [Theory]
    [InlineData("WrongPassword!42")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Email_unbinding_rejects_invalid_password_without_changing_email_or_revoking_credentials(string password)
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UnbindEmailAsync(
            fixture.User.Id, password, null, default));

        Assert.Equal("user@example.test", fixture.User.Email);
        Assert.Equal(0, fixture.UserUpdates);
        Assert.Equal(0, fixture.TokenRevocations);
        Assert.Equal(0, fixture.SessionRevocations);
    }

    [Fact]
    public async Task Email_unbinding_requires_totp_when_enabled()
    {
        var fixture = new Fixture(totpEnabled: true, totpValid: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UnbindEmailAsync(
            fixture.User.Id, Fixture.CurrentPassword, "000000", default));
        Assert.Equal("user@example.test", fixture.User.Email);
        Assert.Equal(0, fixture.UserUpdates);
        Assert.Equal(0, fixture.TokenRevocations);
        Assert.Equal(0, fixture.SessionRevocations);

        fixture.TotpValid = true;
        await fixture.Service.UnbindEmailAsync(fixture.User.Id, Fixture.CurrentPassword, "123456", default);

        Assert.Null(fixture.User.Email);
        Assert.Equal(1, fixture.UserUpdates);
        Assert.Equal(1, fixture.TokenRevocations);
        Assert.Equal(1, fixture.SessionRevocations);
    }

    [Fact]
    public async Task Email_unbinding_rejects_accounts_without_a_bound_email()
    {
        var fixture = new Fixture(hasEmail: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UnbindEmailAsync(
            fixture.User.Id, Fixture.CurrentPassword, null, default));

        Assert.Equal("当前账号尚未绑定邮箱。", exception.Message);
        Assert.Null(fixture.User.Email);
        Assert.Equal(0, fixture.UserUpdates);
        Assert.Equal(0, fixture.TokenRevocations);
        Assert.Equal(0, fixture.SessionRevocations);
    }

    [Fact]
    public async Task Changing_password_invalidates_binding_for_an_outstanding_verification_code()
    {
        var fixture = new Fixture(hasEmail: false);
        await fixture.Service.SendVerificationAsync(
            fixture.User.Id, "new@example.test", Fixture.CurrentPassword, null, default);
        var code = fixture.Sender.LastCode;
        fixture.User.ChangePassword("ChangedPassword!73");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.VerifyEmailAsync(
            fixture.User.Id, "new@example.test", code, default));
        Assert.Null(fixture.User.Email);
        Assert.Equal(0, fixture.UserUpdates);
        Assert.Equal(0, fixture.TokenRevocations);
        Assert.Equal(0, fixture.SessionRevocations);
    }

    [Fact]
    public async Task Reset_requires_totp_and_does_not_consume_email_code_until_totp_succeeds()
    {
        var fixture = new Fixture(totpEnabled: true, totpValid: false);
        await fixture.Service.SendPublicAsync("user@example.test", "reset-password", default);
        var code = fixture.Sender.LastCode;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ResetPasswordAsync(
            "user@example.test", code, "NewPassword!42", "000000", default));
        Assert.False(fixture.User.VerifyPassword("NewPassword!42"));
        Assert.Equal(0, fixture.TokenRevocations);

        fixture.TotpValid = true;
        await fixture.Service.ResetPasswordAsync("user@example.test", code, "NewPassword!42", "123456", default);
        Assert.True(fixture.User.VerifyPassword("NewPassword!42"));
        Assert.Equal(1, fixture.TokenRevocations);
        Assert.Equal(1, fixture.SessionRevocations);
    }

    [Fact]
    public async Task Public_send_is_generic_for_unknown_accounts()
    {
        var fixture = new Fixture(userExists: false);

        await fixture.Service.SendPublicAsync("unknown@example.test", "login", default);

        Assert.Empty(fixture.Sender.Messages);
    }

    [Fact]
    public void User_ef_model_does_not_map_email_verification_state()
    {
        var options = new DbContextOptionsBuilder<NexusAuth.Persistence.DbContext>()
            .UseNpgsql("Host=localhost;Database=metadata-only;Username=test;Password=test")
            .Options;
        using var context = new NexusAuth.Persistence.DbContext(options);

        var user = context.Model.FindEntityType(typeof(User));
        Assert.NotNull(user);
        Assert.Null(user!.FindProperty("EmailVerifiedAt"));
        var usersTable = StoreObjectIdentifier.Table("users", "nexusauth");
        Assert.DoesNotContain(user.GetProperties().Select(property => property.GetColumnName(usersTable)),
            column => column == "email_verified_at");
    }

    private sealed class Fixture
    {
        public const string CurrentPassword = "CurrentPassword!42";
        private readonly IServiceProvider provider;
        private readonly IUserRepository users;
        private readonly IUserService userService;
        private readonly ISecurityPolicyService policy;
        private readonly ITotpService totp;
        private readonly ITokenService tokens;
        private readonly ISsoSessionService sessions;
        private readonly bool userExists;

        public Fixture(bool hasEmail = true, bool active = true, bool totpEnabled = false, bool totpValid = true, bool userExists = true)
        {
            this.userExists = userExists;
            User = User.Create("alice", CurrentPassword, "Alice", hasEmail ? "user@example.test" : null);
            if (!active) User.UpdateScimProfile("alice", "Alice", false, email: User.Email);
            TotpValid = totpValid;

            users = Proxy<IUserRepository>.Create((method, args) => method.Name switch
            {
                nameof(IUserRepository.FindByIdAsync) => args![0] is Guid id && id == User.Id ? User : null,
                nameof(IUserRepository.FindByEmailAsync) => userExists &&
                    string.Equals(args![0]?.ToString(), User.Email, StringComparison.OrdinalIgnoreCase) ? User : null,
                nameof(IUserRepository.UpdateAsync) => UpdateUser(),
                _ => null
            });
            userService = Proxy<IUserService>.Create((method, args) => method.Name switch
            {
                nameof(IUserService.ValidateCredentialsAsync) =>
                    args![1]?.ToString() == CurrentPassword ? User : null,
                _ => null
            });
            policy = Proxy<ISecurityPolicyService>.Create((method, _) =>
                method.Name == nameof(ISecurityPolicyService.CheckUser)
                    ? User.IsActive ? PolicyCheckResult.Success() : PolicyCheckResult.Failure("inactive")
                    : null);
            totp = Proxy<ITotpService>.Create((method, _) => method.Name switch
            {
                nameof(ITotpService.IsEnabledAsync) => totpEnabled,
                nameof(ITotpService.ValidateAsync) => TotpValid,
                _ => null
            });
            tokens = Proxy<ITokenService>.Create((method, _) =>
            {
                if (method.Name == nameof(ITokenService.RevokeAllUserTokensAsync)) TokenRevocations++;
                return null;
            });
            sessions = Proxy<ISsoSessionService>.Create((method, _) =>
            {
                if (method.Name == nameof(ISsoSessionService.RevokeAllForUserAsync)) SessionRevocations++;
                return null;
            });

            var services = new ServiceCollection();
            services.AddMemoryCache();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IEmailSender>(Sender);
            provider = services.BuildServiceProvider();
            var codes = new EmailVerificationCodeService(
                provider.GetRequiredService<IMemoryCache>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new SmtpOptions { Enabled = true }),
                Clock);
            Service = new EmailAccountService(
                users, userService, codes, policy, totp, tokens, sessions,
                NullLogger<EmailAccountService>.Instance);
        }

        public User User { get; }
        public RecordingSender Sender { get; } = new();
        public ManualTimeProvider Clock { get; } = new();
        public EmailAccountService Service { get; }
        public bool TotpValid { get; set; }
        public int UserUpdates { get; private set; }
        public int TokenRevocations { get; set; }
        public int SessionRevocations { get; set; }

        private object? UpdateUser()
        {
            UserUpdates++;
            return null;
        }
    }

    private sealed class RecordingSender : IEmailSender
    {
        private static readonly Regex CodeRegex = new(@"\b(\d{6})\b", RegexOptions.Compiled);
        public List<(string To, string Subject, string Body)> Messages { get; } = [];
        public string LastCode => Regex.Match(Messages[^1].Body, CodeRegex.ToString()).Groups[1].Value;

        public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
        {
            Messages.Add((to, subject, textBody));
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    public class Proxy<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[]?, object?> handler = null!;

        public static T Create(Func<MethodInfo, object?[]?, object?> handler)
        {
            var instance = DispatchProxy.Create<T, Proxy<T>>();
            ((Proxy<T>)(object)instance).handler = handler;
            return instance;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var result = handler(targetMethod, args);
            if (targetMethod.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (targetMethod.ReturnType.IsGenericType && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result]);
            }
            return result;
        }
    }
}
