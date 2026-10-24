namespace NexusAuth.Workbench.Api.Authorization;

public static class WorkbenchAuthorizationPolicies
{
    // Session operations remain available to authenticated users without management access.
    public const string AuthenticatedUser = "WorkbenchAuthenticatedUser";
}
