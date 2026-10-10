using NexusAuth.Shared.Email;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Email;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class EmailVerificationCodeServiceTests
{
    [Fact]
    public async Task Sends_six_digit_code_normalizes_email_and_consumes_once()
    {
        var fixture = CreateFixture();

        await fixture.Service.SendAsync("  User@Example.Test ", "verify-email", "user-42");

        var message = Assert.Single(fixture.Sender.Messages);
        Assert.Equal("user@example.test", message.To);
        var code = ExtractCode(message.Body);
        Assert.Matches("^\\d{6}$", code);
        Assert.True(fixture.Service.TryConsume("USER@example.test", "verify-email", "user-42", code));
        Assert.False(fixture.Service.TryConsume("user@example.test", "verify-email", "user-42", code));
    }

    [Fact]
    public async Task Purpose_and_binding_are_part_of_the_challenge()
    {
        var fixture = CreateFixture();
        await fixture.Service.SendAsync("user@example.test", "reset-password", "account-7");
        var code = ExtractCode(Assert.Single(fixture.Sender.Messages).Body);

        Assert.False(fixture.Service.TryConsume("user@example.test", "login", "account-7", code));
        Assert.False(fixture.Service.TryConsume("user@example.test", "reset-password", "account-8", code));
        Assert.True(fixture.Service.TryConsume("user@example.test", "reset-password", "account-7", code));
    }

    [Fact]
    public async Task Cooldown_is_shared_between_purposes_but_challenges_are_isolated()
    {
        var fixture = CreateFixture();
        await fixture.Service.SendAsync("user@example.test", "login", "signin");
        var oldCode = ExtractCode(fixture.Sender.Messages[0].Body);
        var cooldown = await Assert.ThrowsAsync<EmailCodeRequestException>(() =>
            fixture.Service.SendAsync("user@example.test", "verify-email", "account-7"));
        Assert.Equal(60, cooldown.RetryAfterSeconds);
        Assert.False(cooldown.HasValidCode);
        Assert.Contains("60 秒", cooldown.Message);
        Assert.Single(fixture.Sender.Messages);

        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.Service.SendAsync("user@example.test", "verify-email", "account-7");
        var newCode = ExtractCode(fixture.Sender.Messages[1].Body);
        Assert.True(fixture.Service.TryConsume("user@example.test", "login", "signin", oldCode));
        Assert.True(fixture.Service.TryConsume("user@example.test", "verify-email", "account-7", newCode));
    }

    [Fact]
    public async Task Existing_valid_code_is_reused_instead_of_resending()
    {
        var fixture = CreateFixture();
        await fixture.Service.SendAsync("user@example.test", "login", "signin");
        var oldCode = ExtractCode(fixture.Sender.Messages[0].Body);
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        var existing = await Assert.ThrowsAsync<EmailCodeRequestException>(() =>
            fixture.Service.SendAsync("user@example.test", "login", "signin"));

        Assert.Contains("存在有效的验证码", existing.Message);
        Assert.Equal(0, existing.RetryAfterSeconds);
        Assert.True(existing.HasValidCode);
        Assert.Single(fixture.Sender.Messages);
        Assert.True(fixture.Service.TryConsume("user@example.test", "login", "signin", oldCode));
    }

    [Fact]
    public async Task Expired_code_can_be_resent_at_five_minute_boundary()
    {
        var fixture = CreateFixture();
        await fixture.Service.SendAsync("user@example.test", "login", "signin");
        var oldCode = ExtractCode(fixture.Sender.Messages[0].Body);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        await fixture.Service.SendAsync("user@example.test", "login", "signin");

        Assert.Equal(2, fixture.Sender.Messages.Count);
        Assert.False(fixture.Service.TryConsume("user@example.test", "login", "signin", oldCode));
    }

    [Fact]
    public async Task Consumed_code_can_be_resent_after_cooldown()
    {
        var fixture = CreateFixture();
        await fixture.Service.SendAsync("user@example.test", "login", "signin");
        var code = ExtractCode(fixture.Sender.Messages[0].Body);
        Assert.True(fixture.Service.TryConsume("user@example.test", "login", "signin", code));
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));

        await fixture.Service.SendAsync("user@example.test", "login", "signin");

        Assert.Equal(2, fixture.Sender.Messages.Count);
    }

    [Fact]
    public async Task Fifth_wrong_code_invalidates_challenge()
    {
        var fixture = CreateFixture();
        await fixture.Service.SendAsync("user@example.test", "login", "signin");
        var code = ExtractCode(Assert.Single(fixture.Sender.Messages).Body);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var attempt = 0; attempt < 5; attempt++)
            Assert.False(fixture.Service.TryConsume("user@example.test", "login", "signin", wrong));

        Assert.False(fixture.Service.TryConsume("user@example.test", "login", "signin", code));
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.Service.SendAsync("user@example.test", "login", "signin");
        Assert.Equal(2, fixture.Sender.Messages.Count);
    }

    [Fact]
    public async Task Challenge_expires_after_five_minutes()
    {
        var fixture = CreateFixture();
        await fixture.Service.SendAsync("user@example.test", "verify-email", "account-7");
        var code = ExtractCode(Assert.Single(fixture.Sender.Messages).Body);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.False(fixture.Service.TryConsume("user@example.test", "verify-email", "account-7", code));
    }

    [Fact]
    public async Task Configured_cooldown_and_lifetime_are_used_for_challenges()
    {
        var fixture = CreateFixture(cooldownSeconds: 120, lifetimeMinutes: 10);
        await fixture.Service.SendAsync("user@example.test", "login", "signin");
        var firstCode = ExtractCode(fixture.Sender.Messages[0].Body);
        Assert.Equal(120, fixture.Service.CooldownSeconds);
        Assert.Equal(10, fixture.Service.LifetimeMinutes);
        Assert.Contains("10 分钟", fixture.Sender.Messages[0].Body);

        var cooldown = await Assert.ThrowsAsync<EmailCodeRequestException>(() =>
            fixture.Service.SendAsync("user@example.test", "login", "signin"));
        Assert.Equal(120, cooldown.RetryAfterSeconds);
        Assert.False(cooldown.HasValidCode);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var stillValid = await Assert.ThrowsAsync<EmailCodeRequestException>(() =>
            fixture.Service.SendAsync("user@example.test", "login", "signin"));
        Assert.True(stillValid.HasValidCode);
        Assert.Single(fixture.Sender.Messages);

        fixture.Clock.Advance(TimeSpan.FromMinutes(8));
        await fixture.Service.SendAsync("user@example.test", "login", "signin");
        Assert.Equal(2, fixture.Sender.Messages.Count);
        Assert.False(fixture.Service.TryConsume("user@example.test", "login", "signin", firstCode));
    }

    [Fact]
    public async Task At_most_ten_messages_per_email_per_hour_across_purposes()
    {
        var fixture = CreateFixture();
        for (var i = 0; i < 10; i++)
        {
            await fixture.Service.SendAsync("user@example.test", i % 2 == 0 ? "login" : "verify-email", $"binding-{i}");
            fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SendAsync("user@example.test", "reset-password", "account-7"));
        Assert.Equal(10, fixture.Sender.Messages.Count);
        fixture.Clock.Advance(TimeSpan.FromMinutes(51));
        await fixture.Service.SendAsync("user@example.test", "reset-password", "account-7");
        Assert.Equal(11, fixture.Sender.Messages.Count);
    }

    [Fact]
    public async Task Send_failure_removes_challenge_but_keeps_cooldown_and_send_quota()
    {
        var fixture = CreateFixture();
        fixture.Sender.Fail = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.SendAsync("user@example.test", "login", "signin"));
        fixture.Sender.Fail = false;
        var cooldown = await Assert.ThrowsAsync<EmailCodeRequestException>(() =>
            fixture.Service.SendAsync("user@example.test", "login", "signin"));
        Assert.Equal(60, cooldown.RetryAfterSeconds);
        Assert.False(cooldown.HasValidCode);
        Assert.False(fixture.Service.TryConsume("user@example.test", "login", "signin", "000000"));
        Assert.Single(fixture.Sender.Messages);
    }

    [Fact]
    public async Task Cooldown_exception_reports_remaining_seconds()
    {
        var fixture = CreateFixture();
        await fixture.Service.SendAsync("user@example.test", "login", "signin");
        fixture.Clock.Advance(TimeSpan.FromSeconds(17));

        var exception = await Assert.ThrowsAsync<EmailCodeRequestException>(() =>
            fixture.Service.SendAsync("user@example.test", "login", "signin"));

        Assert.Contains("43 秒", exception.Message);
        Assert.Equal(43, exception.RetryAfterSeconds);
        Assert.False(exception.HasValidCode);
        Assert.Single(fixture.Sender.Messages);
    }

    [Fact]
    public async Task Unsupported_purpose_and_invalid_single_address_are_rejected()
    {
        var fixture = CreateFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SendAsync("user@example.test", "other", "binding"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SendAsync("a@example.test,b@example.test", "login", "binding"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SendAsync("Name <user@example.test>", "login", "binding"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SendAsync(new string('a', 250) + "@example.test", "login", "binding"));
        Assert.Empty(fixture.Sender.Messages);
    }

    [Theory]
    [InlineData("verify-email", "邮箱绑定")]
    [InlineData("reset-password", "密码找回")]
    [InlineData("login", "登录")]
    public async Task Email_body_describes_purpose_without_leaking_binding(string purpose, string label)
    {
        var fixture = CreateFixture();
        await fixture.Service.SendAsync("user@example.test", purpose, "private-binding-value");
        var message = Assert.Single(fixture.Sender.Messages);

        Assert.Contains(label, message.Body);
        Assert.DoesNotContain("private-binding-value", message.Body);
    }

    [Fact]
    public async Task Disabled_smtp_reports_unavailable_and_does_not_send()
    {
        var fixture = CreateFixture(enabled: false);

        Assert.False(fixture.Service.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendAsync("user@example.test", "login", "signin"));
        Assert.Empty(fixture.Sender.Messages);
    }

    private static Fixture CreateFixture(bool enabled = true, int cooldownSeconds = 60, int lifetimeMinutes = 5)
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(new ManualTimeProvider());
        services.AddSingleton<IOptions<SmtpOptions>>(Options.Create(new SmtpOptions
        {
            Enabled = enabled,
            VerificationCodeCooldownSeconds = cooldownSeconds,
            VerificationCodeLifetimeMinutes = lifetimeMinutes
        }));
        var sender = new RecordingSender();
        services.AddSingleton<IEmailSender>(sender);
        var provider = services.BuildServiceProvider();
        return new Fixture(
            new EmailVerificationCodeService(
                provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IOptions<SmtpOptions>>(),
                provider.GetRequiredService<TimeProvider>()),
            sender,
            (ManualTimeProvider)provider.GetRequiredService<TimeProvider>());
    }

    private static string ExtractCode(string body)
    {
        var match = Regex.Match(body, @"\b(\d{6})\b");
        Assert.True(match.Success, "The email body should contain one six-digit verification code.");
        return match.Groups[1].Value;
    }

    private sealed record Fixture(EmailVerificationCodeService Service, RecordingSender Sender, ManualTimeProvider Clock);

    private sealed class RecordingSender : IEmailSender
    {
        public List<RecordedMessage> Messages { get; } = [];
        public bool Fail { get; set; }

        public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
        {
            Messages.Add(new RecordedMessage(to, subject, textBody));
            return Fail ? Task.FromException(new IOException("Simulated delivery failure.")) : Task.CompletedTask;
        }
    }

    private sealed record RecordedMessage(string To, string Subject, string Body);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
