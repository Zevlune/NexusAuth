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
}
