namespace NexusAuth.Shared.Email;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default);
}
