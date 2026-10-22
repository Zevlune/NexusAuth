using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using NexusAuth.Application.Users;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Application.Services.Email;

public sealed class EmailAccountService(
    IUserRepository users, IUserService userService, EmailVerificationCodeService codes,
    ISecurityPolicyService policy, ITotpService totp, ITokenService tokens, ISsoSessionService sessions,
    ILogger<EmailAccountService> logger)
{
    /// <summary>
    /// 中文注释：找回密码的所有失败（账号不存在/不可用、邮箱验证码错误、动态码错误）统一返回这一条提示，
    /// 避免通过不同错误信息判断邮箱是否已注册或账号是否绑定了身份验证器。
    /// </summary>
    public const string ResetFailedMessage = "验证码或动态码无效、已过期，或账号不可用。";

    public bool IsAvailable => codes.IsAvailable;
    public int CooldownSeconds => codes.CooldownSeconds;
    public int LifetimeMinutes => codes.LifetimeMinutes;
    public static string Binding(User user) => user.Id + ":" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(user.PasswordHash + ":" + user.Email + ":" + user.TokenInvalidBefore?.ToUnixTimeSeconds())));

    public async Task SendPublicAsync(string email, string purpose, CancellationToken ct)
    {
        if (purpose is not ("login" or "reset-password")) throw new ArgumentException("验证码用途无效。");
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("请输入邮箱地址。");
        if (!IsAvailable)
        {
            logger.LogWarning("Email code request rejected because SMTP is disabled. Purpose={Purpose}", purpose);
            throw new InvalidOperationException("邮件发送尚未配置。");
        }
        var user = await users.FindByEmailAsync(email.Trim(), ct);
        if (!Eligible(user))
        {
            logger.LogInformation("Email code request skipped because account is unavailable. Purpose={Purpose}", purpose);
            return; // The page always returns the same acknowledgement.
        }
        try { await codes.SendAsync(user!.Email!, purpose, Binding(user), ct); }
        catch (EmailCodeRequestException e)
        {
            logger.LogInformation("Email code request not sent. Purpose={Purpose} RetryAfterSeconds={RetryAfterSeconds} HasValidCode={HasValidCode}",
                purpose, e.RetryAfterSeconds, e.HasValidCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) {
            // Unknown accounts, cooldowns and delivery failures share the public response.
            logger.LogWarning(e, "Email challenge delivery did not complete. Purpose={Purpose} FailureType={FailureType}",
                purpose, e.GetType().Name);
        }
    }

    public async Task<User?> AuthenticateAsync(string email, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code)) return null;
        var user = await users.FindByEmailAsync(email.Trim(), ct);
        if (!Eligible(user) || !codes.TryConsume(user!.Email!, "login", Binding(user), code)) return null;
        return user;
    }

    public async Task SendVerificationAsync(Guid id, string email, string password, string? otp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("请输入邮箱地址。");
        var user = await ValidateAccountPasswordAsync(id, password, otp, ct);
        var other = await users.FindByEmailAsync(email.Trim(), ct);
        if (other is not null && other.Id != id) throw new InvalidOperationException("此邮箱无法绑定。");
        try { await codes.SendAsync(email, "verify-email", Binding(user), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ArgumentException) { throw; }
        catch (EmailCodeRequestException) { throw; }
        catch (Exception e) {
            logger.LogWarning(e, "Email verification delivery did not complete. UserId={UserId} FailureType={FailureType}",
                id, e.GetType().Name);
            throw new InvalidOperationException("暂时无法发送验证码，请检查邮件配置或稍后重试。");
        }
    }

    private async Task<User> ValidateAccountPasswordAsync(Guid id, string password, string? otp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("请输入当前密码。");
        var user = await users.FindByIdAsync(id, ct) ?? throw new InvalidOperationException("账号不存在。");
        var authenticated = await userService.ValidateCredentialsAsync(user.Username, password, ct);
        if (authenticated?.Id != id || !policy.CheckUser(user).IsSuccess)
            throw new InvalidOperationException("当前密码无效或账号不可用。");
        if (await totp.IsEnabledAsync(id, ct) && !await totp.ValidateAsync(id, otp ?? "", ct))
            throw new InvalidOperationException("动态验证码无效。");
        return user;
    }

    public async Task UnbindEmailAsync(Guid id, string password, string? otp, CancellationToken ct)
    {
        var user = await ValidateAccountPasswordAsync(id, password, otp, ct);
        if (string.IsNullOrWhiteSpace(user.Email)) throw new InvalidOperationException("当前账号尚未绑定邮箱。");
        user.UnbindEmail();
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        await users.UpdateAsync(user, ct);
        await tokens.RevokeAllUserTokensAsync(id, ct);
        await sessions.RevokeAllForUserAsync(id, ct);
    }

    public async Task VerifyEmailAsync(Guid id, string email, string code, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id, ct) ?? throw new InvalidOperationException("账号不存在。");
        if (!user.IsActive || !policy.CheckUser(user).IsSuccess || string.IsNullOrWhiteSpace(email) ||
            !codes.TryConsume(email, "verify-email", Binding(user), code))
            throw new InvalidOperationException("验证码无效、过期或已使用。");
        var other = await users.FindByEmailAsync(email.Trim(), ct);
        if (other is not null && other.Id != id) throw new InvalidOperationException("此邮箱无法绑定。");
        user.BindEmail(email);
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        await users.UpdateAsync(user, ct);
        await tokens.RevokeAllUserTokensAsync(id, ct);
        await sessions.RevokeAllForUserAsync(id, ct);
    }

    public async Task ResetPasswordAsync(string email, string code, string password, string? otp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("请输入邮箱地址。");
        PasswordPolicy.EnsureValid(password);
        var user = await users.FindByEmailAsync(email.Trim(), ct);
        // 中文注释：先校验（但不消费）邮箱验证码，再校验动态码。没有邮箱验证码的人无法触达动态码校验，
        // 也就无法借找回密码接口猜测动态码或恶意触发账号锁定；动态码通过后才真正消费邮箱验证码。
        if (!Eligible(user) || string.IsNullOrWhiteSpace(code)
            || !codes.TryVerify(user!.Email!, "reset-password", Binding(user), code))
            throw new InvalidOperationException(ResetFailedMessage);
        if (await totp.IsEnabledAsync(user.Id, ct) && !await totp.ValidateAsync(user.Id, otp ?? "", ct))
            throw new InvalidOperationException(ResetFailedMessage);
        if (!codes.TryConsume(user.Email!, "reset-password", Binding(user), code))
            throw new InvalidOperationException(ResetFailedMessage);
        user.ChangePassword(password);
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        await users.UpdateAsync(user, ct);
        await tokens.RevokeAllUserTokensAsync(user.Id, ct);
        await sessions.RevokeAllForUserAsync(user.Id, ct);
    }

    private bool Eligible(User? user) => user is { IsActive: true } && !string.IsNullOrWhiteSpace(user.Email)
        && !user.IsLoginLocked(DateTimeOffset.UtcNow) && policy.CheckUser(user).IsSuccess;
}
