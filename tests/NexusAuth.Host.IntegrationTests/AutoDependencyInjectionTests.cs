using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Email;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Users;
using NexusAuth.Host.Authentication;
using NexusAuth.Host.TokenEndpoint;
using NexusAuth.Shared.Email;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public class AutoDependencyInjectionTests
{
    [Fact]
    public void Luck_scanning_registers_business_services_and_preserves_stateful_service_lifetimes()
    {
        var services = AutoDependencyServices.Create();
        AssertRegistration<IUserService, UserService>(ServiceLifetime.Scoped);
        AssertRegistration<ITotpService, TotpService>(ServiceLifetime.Scoped);
        AssertRegistration<ITokenService, TokenService>(ServiceLifetime.Scoped);
        AssertRegistration<IWebSignInService, WebSignInService>(ServiceLifetime.Scoped);
        AssertRegistration<IEmailAccountService, EmailAccountService>(ServiceLifetime.Scoped);
        AssertRegistration<IRefreshTokenClientStrategyResolver, RefreshTokenClientStrategyResolver>(ServiceLifetime.Scoped);
        AssertRegistration<ISsoCookieAuthenticationEvents, SsoCookieAuthenticationEvents>(ServiceLifetime.Scoped);
        AssertRegistration<ILoginFlowStateProtector, LoginFlowStateProtector>(ServiceLifetime.Singleton);
        AssertRegistration<IWebAuthnEnrollmentStateProtector, WebAuthnEnrollmentStateProtector>(ServiceLifetime.Singleton);
        AssertRegistration<ISliderCaptchaChallengeProtector, SliderCaptchaChallengeProtector>(ServiceLifetime.Singleton);
        AssertRegistration<ITotpSecretProtector, TotpSecretProtector>(ServiceLifetime.Singleton);
        AssertRegistration<IEmailVerificationCodeService, EmailVerificationCodeService>(ServiceLifetime.Singleton);
        AssertRegistration<ITokenSigningCredentialsProvider, TokenSigningCredentialsProvider>(ServiceLifetime.Singleton);
        AssertRegistration<IIdTokenHintValidator, IdTokenHintValidator>(ServiceLifetime.Singleton);
        AssertRegistration<ITokenSigningKeySource, CertificateTokenSigningKeySource>(ServiceLifetime.Singleton);
        AssertRegistration<ITokenSigningKeySource, RsaKeyFileTokenSigningKeySource>(ServiceLifetime.Singleton);
        AssertRegistration<IEmailSender, SmtpEmailSender>(ServiceLifetime.Transient);
        AssertRegistration<ISmtpTransport, MailKitSmtpTransport>(ServiceLifetime.Transient);
        AssertRegistration<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>(ServiceLifetime.Singleton);
        AssertRegistration<IValidateOptions<LoginFlowOptions>, LoginFlowOptionsValidator>(ServiceLifetime.Singleton);
        AssertRegistration<IValidateOptions<WebAuthnOptions>, WebAuthnOptionsValidator>(ServiceLifetime.Singleton);
        AssertRegistration<IValidateOptions<SliderCaptchaOptions>, SliderCaptchaOptionsValidator>(ServiceLifetime.Singleton);

        void AssertRegistration<TService, TImplementation>(ServiceLifetime lifetime)
        {
            var descriptor = Assert.Single(services, descriptor => !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(TService) && descriptor.ImplementationType == typeof(TImplementation));
            Assert.Equal(lifetime, descriptor.Lifetime);
        }
    }

    [Fact]
    public void Keyed_grant_strategies_and_refresh_resolver_are_shared_within_each_scope_and_isolated_between_scopes()
    {
        var services = AutoDependencyServices.Create();
        services.AddOptions();
        services.AddTokenGrantStrategies();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var strategy = first.ServiceProvider.GetRequiredKeyedService<ITokenGrantStrategy>(TokenGrantKeys.Unsupported);
        var resolver = first.ServiceProvider.GetRequiredService<IRefreshTokenClientStrategyResolver>();
        Assert.IsType<UnsupportedTokenGrantStrategy>(strategy);
        Assert.Same(strategy, first.ServiceProvider.GetRequiredService<UnsupportedTokenGrantStrategy>());
        Assert.Same(strategy, first.ServiceProvider.GetRequiredKeyedService<ITokenGrantStrategy>(TokenGrantKeys.Unsupported));
        Assert.NotSame(strategy, second.ServiceProvider.GetRequiredKeyedService<ITokenGrantStrategy>(TokenGrantKeys.Unsupported));
        Assert.Same(resolver, first.ServiceProvider.GetRequiredService<IRefreshTokenClientStrategyResolver>());
        Assert.NotSame(resolver, second.ServiceProvider.GetRequiredService<IRefreshTokenClientStrategyResolver>());
        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredKeyedService<ITokenGrantStrategy>(TokenGrantKeys.Unsupported));
    }

    [Fact]
    public void Captcha_is_consumed_only_once_across_request_scopes()
    {
        var services = AutoDependencyServices.Create();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddMemoryCache();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var issuer = first.ServiceProvider.GetRequiredService<ISliderCaptchaChallengeProtector>();
        var consumer = second.ServiceProvider.GetRequiredService<ISliderCaptchaChallengeProtector>();
        Assert.Same(issuer, consumer);
        var challenge = new SliderCaptchaChallenge(100, Guid.NewGuid().ToString("N"));
        var token = issuer.Protect(challenge, TimeSpan.FromMinutes(1));
        Assert.True(consumer.TryValidate(token, 100));
        Assert.False(issuer.TryValidate(token, 100));
    }
}
