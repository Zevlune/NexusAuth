using Microsoft.AspNetCore.Mvc;

namespace NexusAuth.Workbench.Api.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class WorkbenchApiResultAttribute : TypeFilterAttribute
{
    public WorkbenchApiResultAttribute()
        : base(typeof(WorkbenchApiResultFilter))
    {
    }
}
