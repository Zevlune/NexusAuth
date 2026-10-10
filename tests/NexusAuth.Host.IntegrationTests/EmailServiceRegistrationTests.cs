using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Application.Services.Email;
using NexusAuth.Application.Users;
using NexusAuth.Shared.Email;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class EmailServiceRegistrationTests
{
    [Fact]
    public void Shared_email_registration_resolves_without_host_and_reloads_timing_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Enabled"] = "false",
            ["Smtp:VerificationCodeCooldownSeconds"] = "60",
            ["Smtp:VerificationCodeLifetimeMinutes"] = "5"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusAuthEmail(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<SmtpEmailSender>(provider.GetRequiredService<IEmailSender>());
        Assert.Equal(typeof(SmtpOptions).Assembly, typeof(IEmailSender).Assembly);
        Assert.NotEqual(typeof(AppWebModule).Assembly, typeof(SmtpEmailSender).Assembly);
        var codes = provider.GetRequiredService<EmailVerificationCodeService>();
        Assert.Equal(60, codes.CooldownSeconds);
        Assert.Equal(5, codes.LifetimeMinutes);

        configuration["Smtp:VerificationCodeCooldownSeconds"] = "120";
        configuration["Smtp:VerificationCodeLifetimeMinutes"] = "10";
        configuration.Reload();
        Assert.Equal(120, codes.CooldownSeconds);
        Assert.Equal(10, codes.LifetimeMinutes);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(3601, 60)]
    [InlineData(60, 0)]
    [InlineData(60, 61)]
    [InlineData(121, 2)]
    public void Invalid_email_timing_settings_are_rejected(int cooldown, int lifetime)
    {
        var options = new SmtpOptions
        {
            Enabled = true, Host = "smtp.example.test", Username = "sender@example.test",
            Password = "test-value", FromAddress = "sender@example.test",
            VerificationCodeCooldownSeconds = cooldown, VerificationCodeLifetimeMinutes = lifetime
        };
        Assert.True(new SmtpOptionsValidator().Validate(null, options).Failed);
    }
}
