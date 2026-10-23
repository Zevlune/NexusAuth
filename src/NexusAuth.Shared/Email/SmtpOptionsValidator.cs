using Luck.Framework.Infrastructure.DependencyInjectionModule;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace NexusAuth.Shared.Email;

public class SmtpOptionsValidator : IValidateOptions<SmtpOptions>, ISingletonDependency
{
    public ValidateOptionsResult Validate(string? name, SmtpOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var errors = new List<string>();
        if (options.VerificationCodeCooldownSeconds is < 1 or > 3600)
            errors.Add("Smtp:VerificationCodeCooldownSeconds must be between 1 and 3600.");
        if (options.VerificationCodeLifetimeMinutes is < 1 or > 60)
            errors.Add("Smtp:VerificationCodeLifetimeMinutes must be between 1 and 60.");
        if (options.VerificationCodeCooldownSeconds > options.VerificationCodeLifetimeMinutes * 60)
            errors.Add("SMTP verification code cooldown must not exceed its lifetime.");
        if (string.IsNullOrWhiteSpace(options.Host))
            errors.Add("Smtp:Host is required.");
        if (options.Port is < 1 or > 65535)
            errors.Add("Smtp:Port must be between 1 and 65535.");
        if (options.Security is not ("SslOnConnect" or "StartTls"))
            errors.Add("Smtp:Security must be SslOnConnect or StartTls.");
        if (string.IsNullOrWhiteSpace(options.Username))
            errors.Add("Smtp:Username is required.");
        if (string.IsNullOrWhiteSpace(options.Password))
            errors.Add("Smtp:Password is required.");
        if (string.IsNullOrWhiteSpace(options.FromAddress)
            || !MailAddress.TryCreate(options.FromAddress, out var from)
            || from.Address != options.FromAddress)
            errors.Add("Smtp:FromAddress must be a single email address.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
