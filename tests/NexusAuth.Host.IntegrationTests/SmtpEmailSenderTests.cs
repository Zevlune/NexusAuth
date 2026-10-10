using NexusAuth.Shared.Email;
using Microsoft.Extensions.Options;
using System.Text.Json;
using MimeKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusAuth.Application.Services.Email;
using Xunit;
using Xunit.Abstractions;

namespace NexusAuth.Host.IntegrationTests;

public sealed class SmtpEmailSenderTests(ITestOutputHelper output)
{
    [Fact]
    public void Disabled_smtp_does_not_require_connection_settings()
    {
        var result = new SmtpOptionsValidator().Validate(null, new SmtpOptions { Enabled = false });

        Assert.False(result.Failed);
    }

    [Fact]
    public void Enabled_smtp_requires_connection_settings()
    {
        var result = new SmtpOptionsValidator().Validate(null, new SmtpOptions { Enabled = true });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(465, "SslOnConnect")]
    [InlineData(587, "StartTls")]
    public void Enabled_smtp_accepts_supported_tls_modes(int port, string security)
    {
        var result = new SmtpOptionsValidator().Validate(null, ValidOptions(port, security));

        Assert.False(result.Failed);
    }

    [Fact]
    public async Task Send_creates_message_with_configured_sender_and_plain_text_body()
    {
        var options = ValidOptions(587, "StartTls");
        var transport = new RecordingTransport();
        var logger = new RecordingLogger<SmtpEmailSender>();
        var sender = new SmtpEmailSender(Options.Create(options), transport, logger);

        await sender.SendAsync("recipient@example.test", "Test subject", "Test body");

        Assert.Same(options, transport.Options);
        var message = Assert.IsType<MimeMessage>(transport.Message);
        var from = Assert.Single(message.From.Mailboxes);
        Assert.Equal("NexusAuth", from.Name);
        Assert.Equal("sender@example.test", from.Address);
        Assert.Equal("recipient@example.test", Assert.Single(message.To.Mailboxes).Address);
        Assert.Equal("Test subject", message.Subject);
        Assert.Equal("Test body", message.TextBody);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Contains("accepted", log.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(options.Password, log.Combined);
        Assert.DoesNotContain("Test body", log.Combined);
    }

    [Fact]
    public async Task Disabled_smtp_does_not_invoke_transport()
    {
        var transport = new RecordingTransport();
        var sender = new SmtpEmailSender(Options.Create(new SmtpOptions { Enabled = false }), transport, NullLogger<SmtpEmailSender>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync("recipient@example.test", "Subject", "Body"));

        Assert.Null(transport.Message);
    }

    [Fact]
    public async Task Transport_failure_logs_structured_context_and_rethrows_original_exception_without_secrets()
    {
        var options = ValidOptions(465, "SslOnConnect");
        const string verificationCode = "482731";
        const string emailBody = "验证码 482731";
        var failure = new InvalidOperationException("SMTP authentication failed");
        failure.Data["SmtpStage"] = "Authentication";
        var transport = new FailingTransport(failure);
        var logger = new RecordingLogger<SmtpEmailSender>();
        var sender = new SmtpEmailSender(Options.Create(options), transport, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync("recipient@example.test", "验证码", emailBody));

        Assert.Same(failure, thrown);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Same(failure, log.Exception);
        Assert.True(log.HasProperty("Host", options.Host));
        Assert.True(log.HasProperty("Port", options.Port));
        Assert.True(log.HasProperty("Security", options.Security));
        Assert.True(log.HasProperty("Stage", "Authentication"));
        Assert.DoesNotContain(options.Password, log.Combined);
        Assert.DoesNotContain(verificationCode, log.Combined);
        Assert.DoesNotContain(emailBody, log.Combined);
    }

    [Fact]
    public async Task Cancellation_does_not_log_transport_error()
    {
        var transport = new CanceledTransport();
        var logger = new RecordingLogger<SmtpEmailSender>();
        var sender = new SmtpEmailSender(Options.Create(ValidOptions(465, "SslOnConnect")), transport, logger);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendAsync("recipient@example.test", "Subject", "Body", cancellation.Token));

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Configured_live_qq_smtp_can_send_test_message()
    {
        var localConfig = Path.Combine(AppContext.BaseDirectory, "smtp.qq.local.json");
        if (!File.Exists(localConfig))
        {
            output.WriteLine("未发送：找不到 smtp.qq.local.json。");
            return;
        }

        var credentials = JsonSerializer.Deserialize<QqSmtpCredentials>(await File.ReadAllTextAsync(localConfig));
        if (credentials is null
            || string.IsNullOrWhiteSpace(credentials.Username)
            || credentials.Username == "your-qq-number@qq.com"
            || string.IsNullOrWhiteSpace(credentials.AuthorizationCode))
        {
            output.WriteLine("未发送：请在 smtp.qq.local.json 中填写 QQ 发件邮箱和 SMTP 授权码。");
            return;
        }

        var options = new SmtpOptions
        {
            Enabled = true,
            Host = "smtp.qq.com",
            Port = 465,
            Security = "SslOnConnect",
            Username = credentials.Username,
            Password = credentials.AuthorizationCode,
            FromAddress = credentials.Username,
            FromName = "NexusAuth",
        };
        var validation = new SmtpOptionsValidator().Validate(null, options);
        Assert.False(validation.Failed, string.Join("; ", validation.Failures ?? []));

        var sender = new SmtpEmailSender(Options.Create(options),
            new MailKitSmtpTransport(NullLogger<MailKitSmtpTransport>.Instance),
            NullLogger<SmtpEmailSender>.Instance);
        await sender.SendAsync("18790997531@163.com", "NexusAuth QQ SMTP test", "NexusAuth QQ SMTP delivery test.");
        output.WriteLine("QQ SMTP 已完成发送，收件人：18790997531@163.com。");
    }

    private sealed record QqSmtpCredentials(string Username, string AuthorizationCode);

    private static SmtpOptions ValidOptions(int port, string security) => new()
    {
        Enabled = true,
        Host = "smtp.example.test",
        Port = port,
        Username = "smtp-user",
        Password = "test-placeholder-not-a-secret",
        FromAddress = "sender@example.test",
        FromName = "NexusAuth",
        Security = security,
    };

    private sealed class RecordingTransport : ISmtpTransport
    {
        public SmtpOptions? Options { get; private set; }
        public MimeMessage? Message { get; private set; }

        public Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct = default)
        {
            Options = options;
            Message = message;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingTransport(Exception failure) : ISmtpTransport
    {
        public Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct = default) =>
            Task.FromException(failure);
    }

    private sealed class CanceledTransport : ISmtpTransport
    {
        public Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct = default) =>
            Task.FromCanceled(ct);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToArray()
                : [];
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, properties));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception,
        KeyValuePair<string, object?>[] Properties)
    {
        public string Combined => $"{Message} {Exception} {string.Join(' ', Properties.Select(property => $"{property.Key}={property.Value}"))}";

        public bool HasProperty(string name, object? value) => Properties.Any(property =>
            property.Key == name && Equals(property.Value, value));
    }
}
