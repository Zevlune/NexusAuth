namespace NexusAuth.Shared.Email;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 465;
    public string Security { get; set; } = "SslOnConnect";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "NexusAuth";
    public int VerificationCodeCooldownSeconds { get; set; } = 60;
    public int VerificationCodeLifetimeMinutes { get; set; } = 5;
}
