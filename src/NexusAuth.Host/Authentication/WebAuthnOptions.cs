using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;

namespace NexusAuth.Host.Authentication;

/// <summary>
/// WebAuthn Relying Party 配置。RP ID、Origin 与浏览器实际访问域名必须匹配。
/// </summary>
public sealed class WebAuthnOptions
{
    public const string SectionName = "WebAuthn";

    /// <summary>控制 Web 端是否提供 Passkey 注册与登录能力。</summary>
    public bool Enabled { get; set; }
    public string RelyingPartyId { get; set; } = "localhost";
    public string RelyingPartyName { get; set; } = "WebAuthn NexusAuth";
    public List<string> Origins { get; set; } = ["http://localhost:5200"];
    public int ChallengeLifetimeSeconds { get; set; } = 300;
    public UserVerificationRequirement UserVerification { get; set; } = UserVerificationRequirement.Required;
}
