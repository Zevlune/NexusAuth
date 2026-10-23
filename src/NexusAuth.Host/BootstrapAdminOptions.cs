namespace NexusAuth.Host;

public sealed class BootstrapAdminOptions
{
    public string? Username { get; set; }

    public string? Password { get; set; }

    public string Nickname { get; set; } = "System Admin";

    public string? Email { get; set; }

    /// <summary>
    /// 中文注释：同名普通账号已存在时是否将其提升为系统管理员。默认 false：
    /// 自助注册或 SCIM 可能抢先占用配置的管理员用户名，自动提升会把管理员权限交给该账号，
    /// 因此默认拒绝启动；只有运维确认该账号可信后才应显式开启。
    /// </summary>
    public bool PromoteExistingUser { get; set; }
}
