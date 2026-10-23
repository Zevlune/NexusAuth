using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NexusAuth.Host.Authentication;

public sealed class LoginFlowOptions
{
    public const string SectionName = "LoginFlow";

    public int PendingStateLifetimeMinutes { get; set; } = 5;

    public int SessionLifetimeMinutes { get; set; } = 1440;

    public int RememberMeLifetimeDays { get; set; } = 3;

    public bool AllowRememberMe { get; set; } = true;

    public List<LoginFlowStepOptions> Steps { get; set; } = [];

    public void ApplyDefaults()
    {
        Steps ??= [];
        if (Steps.Count > 0)
        {
            foreach (var step in Steps)
            {
                step.Type = step.Type?.Trim().ToLowerInvariant() ?? string.Empty;
                step.Requirement = step.Requirement?.Trim().ToLowerInvariant() ?? string.Empty;
            }
        }
        else
        {
            Steps =
            [
                new() { Type = LoginFlowStepTypes.Password, Requirement = LoginFlowRequirements.Required },
                new() { Type = LoginFlowStepTypes.Totp, Requirement = LoginFlowRequirements.Conditional },
            ];
        }
    }

    public LoginFlowStepOptions? FindStep(string type) =>
        Steps.FirstOrDefault(step => string.Equals(step.Type, type, StringComparison.OrdinalIgnoreCase));

    public AuthenticationProperties CreateAuthenticationProperties(bool rememberMe, DateTimeOffset issuedAt)
    {
        var useRememberMe = AllowRememberMe && rememberMe;
        var lifetime = useRememberMe
            ? TimeSpan.FromDays(RememberMeLifetimeDays)
            : TimeSpan.FromMinutes(SessionLifetimeMinutes);

        return new AuthenticationProperties
        {
            IsPersistent = useRememberMe,
            IssuedUtc = issuedAt,
            ExpiresUtc = issuedAt.Add(lifetime),
            AllowRefresh = useRememberMe ? false : null,
        };
    }
}

public sealed class LoginFlowStepOptions
{
    public string Type { get; set; } = string.Empty;

    public string Requirement { get; set; } = LoginFlowRequirements.Required;
}

public static class LoginFlowStepTypes
{
    public const string Password = "password";
    public const string Totp = "totp";
}

public static class LoginFlowRequirements
{
    public const string Required = "required";
    public const string Conditional = "conditional";
}
