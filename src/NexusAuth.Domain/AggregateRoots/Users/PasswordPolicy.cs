using System.Text;

namespace NexusAuth.Domain.AggregateRoots.Users;

/// <summary>
/// 中文注释：统一的本地账号密码策略，供注册、找回密码、修改密码以及管理员创建/重置密码共用。
/// BCrypt 只使用前 72 个 UTF-8 字节，超出部分会被静默截断，因此上限按字节而不是字符计算。
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;

    public const int MaximumUtf8Bytes = 72;

    public const string RequiredMessage = "请输入密码。";

    public const string TooShortMessage = "密码长度至少为 8 个字符。";

    public const string TooLongMessage = "密码不能超过 72 个 UTF-8 字节。";

    /// <summary>
    /// 返回第一条不满足策略的提示；密码合规时返回 null。
    /// </summary>
    public static string? GetValidationError(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return RequiredMessage;

        if (password.Length < MinimumLength)
            return TooShortMessage;

        if (Encoding.UTF8.GetByteCount(password) > MaximumUtf8Bytes)
            return TooLongMessage;

        return null;
    }

    /// <summary>
    /// 密码不合规时抛出 <see cref="ArgumentException"/>，消息可直接展示给用户。
    /// </summary>
    public static void EnsureValid(string? password, string? paramName = null)
    {
        var error = GetValidationError(password);
        if (error is not null)
            throw new ArgumentException(error, paramName);
    }
}
