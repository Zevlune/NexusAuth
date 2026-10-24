namespace NexusAuth.Workbench.Api.Authorization;

/// <summary>
/// 配置 mock Workbench 权限来源。未来可由读取 Redis 或数据库权限表的
/// <see cref="IWorkbenchPermissionService"/> 实现替换。
/// </summary>
public sealed class MockWorkbenchPermissionOptions
{
    public const string SectionName = "Authorization:MockPermissions";

    // 未配置时沿用系统账号的全部权限；显式空列表表示无权限。
    public List<WorkbenchPermission>? SystemAccountPermissions { get; set; }

    public Dictionary<string, List<WorkbenchPermission>> UserPermissions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
