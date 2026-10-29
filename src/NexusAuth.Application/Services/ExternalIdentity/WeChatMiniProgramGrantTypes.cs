using NexusAuth.Shared.OAuth;

namespace NexusAuth.Application.Services.ExternalIdentity;

/// <summary>历史兼容别名；新代码请直接使用 <see cref="OAuthGrantTypes.WeChatMiniProgram"/>。</summary>
public static class WeChatMiniProgramGrantTypes
{
    public const string GrantType = OAuthGrantTypes.WeChatMiniProgram;
}
