using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Luck.Logging.Serilog;
using NexusAuth.Application.Services.Security;
using NexusAuth.Shared.OAuth;

namespace NexusAuth.Application.Services.ExternalIdentity;

public sealed class WeChatMiniProgramLoginService(
    IHttpClientFactory httpClientFactory,
    IUserRepository userRepository,
    IUserExternalIdentityRepository externalIdentityRepository,
    IOptions<WeChatMiniProgramOptions> options,
    ILogger<WeChatMiniProgramLoginService> logger,
    ISecurityPolicyService? securityPolicyService = null) : IWeChatMiniProgramLoginService
{
    public const string Provider = "wechat_miniprogram";
    public const string HttpClientName = "WeChatMiniProgram";

    private readonly WeChatMiniProgramOptions options = options.Value;

    public async Task<WeChatMiniProgramLoginResult> AuthenticateAsync(
        string code,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (!options.Enabled)
            throw new InvalidOperationException("WeChat mini program login is disabled.");

        if (string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.AppSecret))
            throw new InvalidOperationException("WeChat mini program credentials are not configured.");

        var session = await ExchangeCodeAsync(code.Trim(), ct);
        var authenticatedAt = DateTimeOffset.UtcNow;
        var identity = await externalIdentityRepository.FindBySubjectAsync(
            Provider,
            options.AppId,
            session.OpenId!,
            ct);

        if (identity is not null)
        {
            var existingUser = await userRepository.FindByIdAsync(identity.UserId, ct);
            if (existingUser is null || !existingUser.IsActive)
                throw new WeChatMiniProgramLoginException("invalid_grant", "The linked user account is unavailable.");

            identity.UpdateLastAuthenticatedAt(authenticatedAt);
            await externalIdentityRepository.UpdateAsync(identity, ct);

            logger.LogLuckInformation(
                "External identity authentication succeeded. Provider={Provider} UserId={UserId} Outcome={Outcome}",
                [Provider, existingUser.Id, "ExternalLoginSucceeded"]);

            return new WeChatMiniProgramLoginResult(
                existingUser,
                authenticatedAt,
                Provider,
                options.AppId,
                session.OpenId!,
                session.UnionId);
        }

        if (!options.AutoProvisionUsers)
        {
            logger.LogLuckWarning(
                "External identity login rejected. Provider={Provider} Reason={ReasonCode} Outcome={Outcome}",
                [Provider, "AutoProvisionDisabled", "ExternalLoginRejected"]);
            throw new WeChatMiniProgramLoginException("invalid_grant", "No local account is linked to this WeChat identity.");
        }

        // A random, undisclosed password preserves the existing non-null hash schema.
        // No password credential is supplied to the small program.
        var candidate = User.Create(
            $"wx_{Guid.NewGuid():N}",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            "微信用户");

        // 中文注释：在持久化之前执行用户安全策略（例如白名单模式），
        // 否则每次被拒绝的登录都会留下一个无法使用的 wx_ 账号。
        if (securityPolicyService is not null && !securityPolicyService.CheckUser(candidate).IsSuccess)
        {
            logger.LogLuckWarning(
                "External identity login rejected. Provider={Provider} Reason={ReasonCode} Outcome={Outcome}",
                [Provider, "UserPolicyRejected", "ExternalLoginRejected"]);
            throw new WeChatMiniProgramLoginException("invalid_grant", "The user account is unavailable.");
        }

        var user = await externalIdentityRepository.GetOrCreateUserAsync(
            candidate,
            UserExternalIdentity.Create(candidate.Id, Provider, options.AppId, session.OpenId!, session.UnionId, authenticatedAt),
            ct);
        if (!user.IsActive)
            throw new WeChatMiniProgramLoginException("invalid_grant", "The linked user account is unavailable.");

        logger.LogLuckInformation(
            "External identity login succeeded. Provider={Provider} UserId={UserId} Outcome={Outcome}",
            [Provider, user.Id, "ExternalLoginSucceeded"]);

        return new WeChatMiniProgramLoginResult(
            user,
            authenticatedAt,
            Provider,
            options.AppId,
            session.OpenId!,
            session.UnionId);
    }

    private async Task<WeChatCode2SessionResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var endpoint = options.Code2SessionEndpoint
            + $"?appid={Uri.EscapeDataString(options.AppId)}"
            + $"&secret={Uri.EscapeDataString(options.AppSecret)}"
            + $"&js_code={Uri.EscapeDataString(code)}"
            + $"&grant_type={OAuthGrantTypes.AuthorizationCode}";

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(endpoint, ct);
            if (!response.IsSuccessStatusCode)
                throw Unavailable();
            var payload = await response.Content.ReadFromJsonAsync<WeChatCode2SessionResponse>(cancellationToken: ct);
            if (payload is null)
                throw Unavailable();

            if (payload.ErrCode is not null && payload.ErrCode != 0)
            {
                logger.LogLuckWarning(
                    "WeChat code exchange failed. ErrorCode={ErrorCode} Outcome={Outcome}",
                    [payload.ErrCode, "ExternalCodeExchangeFailed"]);
                if (payload.ErrCode is 40029 or 40163 or 40226)
                    throw new WeChatMiniProgramLoginException("invalid_grant", "The WeChat login code is invalid, expired, or rejected.");
                throw Unavailable();
            }

            if (string.IsNullOrWhiteSpace(payload.OpenId) || payload.OpenId.Length > 256
                || payload.UnionId?.Length > 256)
                throw Unavailable();
            return payload;
        }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (NotSupportedException) { throw Unavailable(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unavailable(); }
    }

    private static WeChatMiniProgramLoginException Unavailable() =>
        new("temporarily_unavailable", "WeChat login service is unavailable. Please try again later.");

    private sealed record WeChatCode2SessionResponse(
        [property: JsonPropertyName("openid")] string? OpenId,
        [property: JsonPropertyName("session_key")] string? SessionKey,
        [property: JsonPropertyName("unionid")] string? UnionId,
        [property: JsonPropertyName("errcode")] int? ErrCode,
        [property: JsonPropertyName("errmsg")] string? ErrMsg);
}
