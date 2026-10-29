namespace NexusAuth.Application.Clients;

using NexusAuth.Shared.OAuth;

public class ClientMetadataService : IClientMetadataService
{
    public Task<ClientMetadataDto> GetAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ClientMetadataDto(
            GetIdentityScopes(),
            GetGrantTypes(),
            GetTokenEndpointAuthMethods()));
    }

    private static List<ClientOptionDto> GetIdentityScopes()
    {
        return
        [
            new ClientOptionDto(OidcScopes.OpenId, $"OpenID（{OidcScopes.OpenId}）", "OIDC 必需作用域，用于请求 ID Token。"),
            new ClientOptionDto(OidcScopes.Profile, $"用户资料（{OidcScopes.Profile}）", "读取用户基础资料声明。"),
            new ClientOptionDto(OidcScopes.Email, $"邮箱（{OidcScopes.Email}）", "读取用户邮箱声明。"),
            new ClientOptionDto(OidcScopes.Phone, $"手机号（{OidcScopes.Phone}）", "读取用户手机号声明。"),
            new ClientOptionDto(OidcScopes.Address, $"地址（{OidcScopes.Address}）", "读取用户地址声明。"),
            new ClientOptionDto(OidcScopes.OfflineAccess, $"离线访问（{OidcScopes.OfflineAccess}）", "允许签发 refresh token。"),
        ];
    }

    private static List<ClientOptionDto> GetGrantTypes()
    {
        return
        [
            new ClientOptionDto(OAuthGrantTypes.AuthorizationCode, $"授权码模式（{OAuthGrantTypes.AuthorizationCode}）", "适用于 Web/BFF/OIDC 登录，推荐配合 PKCE。"),
            new ClientOptionDto(OAuthGrantTypes.ClientCredentials, $"客户端凭证模式（{OAuthGrantTypes.ClientCredentials}）", "适用于机器到机器调用，不代表具体用户。"),
            new ClientOptionDto(OAuthGrantTypes.RefreshToken, $"刷新令牌（{OAuthGrantTypes.RefreshToken}）", "使用 refresh token 换取新的 access token。"),
            new ClientOptionDto(OAuthGrantTypes.DeviceCode, "设备码模式（device_code）",
                "适用于无浏览器或输入受限设备。"),
            new ClientOptionDto(
                OAuthGrantTypes.WeChatMiniProgram,
                "微信小程序登录（wechat_miniprogram）",
                "使用微信小程序一次性 code 换取 NexusAuth 用户令牌。"),
        ];
    }

    private static List<ClientOptionDto> GetTokenEndpointAuthMethods()
    {
        return
        [
            new ClientOptionDto(OAuthClientAuthenticationMethods.ClientSecretBasic, $"Basic 密钥认证（{OAuthClientAuthenticationMethods.ClientSecretBasic}）",
                "通过 Authorization Basic 发送 client_id/client_secret。"),
            new ClientOptionDto(OAuthClientAuthenticationMethods.ClientSecretPost, $"表单密钥认证（{OAuthClientAuthenticationMethods.ClientSecretPost}）",
                "通过请求表单发送 client_id/client_secret。"),
            new ClientOptionDto(OAuthClientAuthenticationMethods.ClientSecretJwt, $"共享密钥 JWT 认证（{OAuthClientAuthenticationMethods.ClientSecretJwt}）",
                "客户端使用共享密钥签名 JWT，服务端使用已登记共享密钥验签。"),
            new ClientOptionDto(OAuthClientAuthenticationMethods.PrivateKeyJwt, $"私钥 JWT 认证（{OAuthClientAuthenticationMethods.PrivateKeyJwt}）",
                "客户端使用私钥签名 JWT，服务端使用已登记 JWKS 验签。"),
            new ClientOptionDto(OAuthClientAuthenticationMethods.None, $"公共客户端（{OAuthClientAuthenticationMethods.None}）",
                "不进行客户端认证，仅适用于受服务端策略限制的公共客户端。"),
        ];
    }
}
