using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Host.Authentication;

public interface IWebAuthnEnrollmentStateProtector : ISingletonDependency
{
    string Protect(WebAuthnEnrollmentState state, TimeSpan lifetime);
    bool TryUnprotect(string? token, out WebAuthnEnrollmentState? state);
}
