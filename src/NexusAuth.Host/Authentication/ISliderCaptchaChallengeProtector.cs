using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Host.Authentication;

public interface ISliderCaptchaChallengeProtector : ISingletonDependency
{
    SliderCaptchaChallengeTicket CreateChallenge();
    string Protect(SliderCaptchaChallenge challenge, TimeSpan lifetime);
    bool TryUnprotect(string? protectedChallenge, out SliderCaptchaChallenge? challenge);
    bool TryValidate(string? protectedChallenge, int? offsetPixels);
    bool TryValidate(string? protectedChallenge, int? offsetPixels, out SliderCaptchaChallenge? challenge);
}
