using Luck.Logging.Serilog;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Services.Sessions;

namespace NexusAuth.Application.Users;

public class UserService(
    IUserRepository userRepository,
    ITokenService tokenService,
    ISsoSessionService sessionService,
    ISecurityPolicyService securityPolicyService,
    IOptions<NexusAuthSecurityOptions> securityOptions,
    ILogger<UserService> logger) : IUserService
{
    // 中文注释：邮箱/手机号冲突统一返回同一条提示且不回显输入值，避免注册接口被用来枚举联系方式。
    public const string ContactUnavailableMessage = "邮箱或手机号无法用于注册，请更换后重试。";
    public const string RegistrationDeniedMessage = "当前安全策略不允许注册此账号。";
    public const string AccountLockedMessage = "Too many failed attempts. Try again later.";

    private const string DummyPasswordHash = "$2a$12$pw856E1CHH3FfcshE0NwCeETGR5hyYaeudBqZfQYCpXdbBuvOpuuy";
    private readonly NexusAuthSecurityOptions _securityOptions = securityOptions.Value;

    public async Task<Guid> RegisterAsync(
        string username,
        string rawPassword,
        string nickname,
        string? email = null,
        string? phoneNumber = null,
        Gender gender = Gender.Unknown,
        string? ethnicity = null,
        CancellationToken ct = default)
    {
        PasswordPolicy.EnsureValid(rawPassword);

        var existing = await userRepository.FindByUsernameAsync(username, ct);
        if (existing is not null)
            throw new InvalidOperationException($"Username '{username}' is already taken.");

        if (email is not null)
        {
            var byEmail = await userRepository.FindByEmailAsync(email, ct);
            if (byEmail is not null)
                throw new InvalidOperationException(ContactUnavailableMessage);
        }

        if (phoneNumber is not null)
        {
            var byPhone = await userRepository.FindByPhoneNumberAsync(phoneNumber, ct);
            if (byPhone is not null)
                throw new InvalidOperationException(ContactUnavailableMessage);
        }

        var user = User.Create(username, rawPassword, nickname, email, phoneNumber, gender, ethnicity);

        // 中文注释：白名单/黑名单模式下不创建永远无法登录的账号，也不让自助注册绕过用户安全策略。
        if (!securityPolicyService.CheckUser(user).IsSuccess)
        {
            logger.LogLuckWarning(
                "User registration rejected. Reason={ReasonCode} Outcome={Outcome}",
                ["UserDeniedByPolicy", "RegistrationRejected"]);

            throw new InvalidOperationException(RegistrationDeniedMessage);
        }

        await userRepository.AddAsync(user, ct);

        logger.LogLuckInformation(
            "User registration succeeded. UserId={UserId} Outcome={Outcome}",
            [user.Id, "RegistrationSucceeded"]);

        return user.Id;
    }

    public async Task<User?> ValidateCredentialsAsync(
        string identifier,
        string rawPassword,
        CancellationToken ct = default)
    {
        var user = await userRepository.FindByUsernameAsync(identifier, ct);

        if (user is null && identifier.Contains('@'))
            user = await userRepository.FindByEmailAsync(identifier, ct);

        user ??= await userRepository.FindByPhoneNumberAsync(identifier, ct);

        if (user is null || !user.IsActive)
        {
            // Keep unknown/inactive accounts on the same expensive password
            // verification path so response timing does not reveal usernames.
            BCrypt.Net.BCrypt.Verify(rawPassword, DummyPasswordHash);

            logger.LogLuckWarning(
                "User authentication failed. Reason={ReasonCode} Outcome={Outcome}",
                ["UserNotFoundOrInactive", "UserNotFoundOrInactive"]);

            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (user.IsLoginLocked(now))
        {
            BCrypt.Net.BCrypt.Verify(rawPassword, DummyPasswordHash);

            logger.LogLuckWarning(
                "User authentication failed. UserId={UserId} Reason={ReasonCode} Outcome={Outcome}",
                [user.Id, "UserLocked", "UserLocked"]);

            return null;
        }

        if (!user.VerifyPassword(rawPassword))
        {
            await userRepository.RegisterFailedLoginAsync(
                user.Id,
                Math.Max(1, _securityOptions.LoginFailureLimit),
                TimeSpan.FromMinutes(Math.Max(1, _securityOptions.LoginLockoutMinutes)),
                now,
                ct);

            logger.LogLuckWarning(
                "User authentication failed. UserId={UserId} Reason={ReasonCode} Outcome={Outcome}",
                [user.Id, "InvalidPassword", "InvalidPassword"]);

            return null;
        }

        // 中文注释：密码正确并不代表整个登录流程完成（可能还要校验动态码），因此这里不再清零失败计数；
        // 计数由 WebSignInService 在真正建立登录会话时清零，避免“重新输入密码”重置动态码的失败次数。
        logger.LogLuckInformation(
            "User authentication succeeded. UserId={UserId} Outcome={Outcome}",
            [user.Id, "LoginSucceeded"]);

        return user;
    }

    public Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        return userRepository.FindByIdAsync(id, ct);
    }

    public async Task ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(currentPassword))
            throw new ArgumentException("请输入当前密码。");
        PasswordPolicy.EnsureValid(newPassword);

        var user = await userRepository.FindByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("User was not found.");

        if (!user.IsActive)
            throw new InvalidOperationException("Inactive users cannot change their password.");

        // 中文注释：修改密码时的当前密码校验与登录共用失败计数和锁定窗口，防止已登录会话被用作密码猜测入口。
        var now = DateTimeOffset.UtcNow;
        if (user.IsLoginLocked(now))
        {
            BCrypt.Net.BCrypt.Verify(currentPassword, DummyPasswordHash);

            logger.LogLuckWarning(
                "Password change rejected. UserId={UserId} Reason={ReasonCode} Outcome={Outcome}",
                [user.Id, "UserLocked", "UserLocked"]);

            throw new InvalidOperationException(AccountLockedMessage);
        }

        if (!user.VerifyPassword(currentPassword))
        {
            await userRepository.RegisterFailedLoginAsync(
                user.Id,
                Math.Max(1, _securityOptions.LoginFailureLimit),
                TimeSpan.FromMinutes(Math.Max(1, _securityOptions.LoginLockoutMinutes)),
                now,
                ct);

            logger.LogLuckWarning(
                "Password change rejected. UserId={UserId} Reason={ReasonCode} Outcome={Outcome}",
                [user.Id, "PasswordChangeCurrentPasswordInvalid", "PasswordChangeCurrentPasswordInvalid"]);

            throw new InvalidOperationException("Current password is incorrect.");
        }

        if (user.VerifyPassword(newPassword))
            throw new InvalidOperationException("New password must differ from the current password.");

        user.ChangePassword(newPassword);
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        await userRepository.UpdateAsync(user, ct);
        await tokenService.RevokeAllUserTokensAsync(user.Id, ct);
        await sessionService.RevokeAllForUserAsync(user.Id, ct);

        logger.LogLuckInformation(
            "User password changed. UserId={UserId} Outcome={Outcome}",
            [user.Id, "PasswordChanged"]);
    }
}
