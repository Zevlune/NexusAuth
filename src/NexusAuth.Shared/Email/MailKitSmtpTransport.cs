using Microsoft.Extensions.Logging;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace NexusAuth.Shared.Email;

public class MailKitSmtpTransport(ILogger<MailKitSmtpTransport> logger) : ISmtpTransport
{
    public async Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct = default)
    {
        var security = options.Security switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "StartTls" => SecureSocketOptions.StartTls,
            _ => throw new InvalidOperationException("SMTP requires TLS."),
        };

        using var client = new SmtpClient { Timeout = 15000 };
        var stage = "connect-tls";
        try
        {
            await client.ConnectAsync(options.Host, options.Port, security, ct);
            stage = "authenticate";
            await client.AuthenticateAsync(options.Username, options.Password, ct);
            stage = "send";
            await client.SendAsync(message, ct);
        }
        catch (Exception exception)
        {
            exception.Data["SmtpStage"] = stage;
            throw;
        }

        // Once SendAsync succeeds, the server has accepted the message. A
        // failed QUIT must not invalidate its code or report delivery failure.
        try { await client.DisconnectAsync(true, ct); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SMTP disconnect failed after email was accepted. Host={Host} Port={Port}",
                options.Host, options.Port);
        }
    }
}
