namespace NexusAuth.Workbench.Api;

/// <summary>Normalizes deployment configuration identically for bootstrap and backchannel authentication.</summary>
internal sealed record WorkbenchClientCredentials(string ClientId, string ClientSecret)
{
    public static WorkbenchClientCredentials FromConfiguration(string? clientId, string? clientSecret) =>
        new(clientId?.Trim() ?? string.Empty, clientSecret?.Trim() ?? string.Empty);
}
