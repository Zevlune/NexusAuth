using System.Security.Claims;

namespace NexusAuth.Workbench.Api;

internal static class WorkbenchPrincipalFactory
{
    public static ClaimsPrincipal Create(ClaimsPrincipal validatedPrincipal)
    {
        var subject = validatedPrincipal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
            throw new InvalidOperationException("The ID token does not contain a subject.");
        var name = validatedPrincipal.FindFirstValue("name")
            ?? validatedPrincipal.FindFirstValue("preferred_username") ?? subject;
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject), new Claim(ClaimTypes.Name, name)],
            WorkbenchAuthenticationDefaults.CookieScheme));
    }
}
