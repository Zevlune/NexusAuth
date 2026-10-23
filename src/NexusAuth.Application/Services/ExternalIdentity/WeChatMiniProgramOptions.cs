namespace NexusAuth.Application.Services.ExternalIdentity;

public sealed class WeChatMiniProgramOptions
{
    public const string SectionName = "WeChat:MiniProgram";

    public bool Enabled { get; set; }

    public string ClientId { get; set; } = "wechat-miniapp";

    public string AppId { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;

    public string Code2SessionEndpoint { get; set; } =
        "https://api.weixin.qq.com/sns/jscode2session";

    /// <summary>
    /// 中文注释：首次登录的微信用户是否自动创建本地账号。关闭后只有已绑定的账号可以登录。
    /// 配置键：WeChat:MiniProgram:AutoProvisionUsers，默认 true 保持现有行为。
    /// </summary>
    public bool AutoProvisionUsers { get; set; } = true;
}
