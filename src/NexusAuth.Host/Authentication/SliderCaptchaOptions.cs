using Microsoft.Extensions.Options;

namespace NexusAuth.Host.Authentication;

public sealed class SliderCaptchaOptions
{
    public const string SectionName = "SliderCaptcha";

    public bool Enabled { get; set; }

    public int ChallengeLifetimeSeconds { get; set; } = 120;

    public int TolerancePixels { get; set; } = 5;

    public int TrackWidthPixels { get; set; } = 300;
}
