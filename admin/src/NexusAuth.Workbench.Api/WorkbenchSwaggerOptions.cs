namespace NexusAuth.Workbench.Api;

public sealed class WorkbenchSwaggerOptions
{
    public const string SectionName = "Swagger";

    /// <summary>When omitted, Swagger is exposed only in Development.</summary>
    public bool? Enabled { get; set; }
}
