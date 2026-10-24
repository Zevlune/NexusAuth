namespace NexusAuth.Workbench.Api.IntegrationTests;

internal sealed class RecordingAuthorizationLogScope : IDisposable
{
    private Action? restore;

    public RecordingAuthorizationLogScope(Action restore)
    {
        this.restore = restore;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref restore, null)?.Invoke();
    }
}
