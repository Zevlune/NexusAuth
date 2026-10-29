namespace NexusAuth.Workbench.Api.Authentication.Tokens;

public interface IWorkbenchTokenRefreshCoordinator
{
    Task<WorkbenchTokenResult> RefreshAsync(string refreshToken, CancellationToken ct);
}
