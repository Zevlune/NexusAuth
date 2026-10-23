using NexusAuth.Shared.Email;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NexusAuth.Application.Services.Email;

public class EmailVerificationCodeService(
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory,
    IOptions<SmtpOptions> smtpOptions,
    TimeProvider timeProvider,
    IOptionsMonitor<SmtpOptions>? liveOptions = null) : IEmailVerificationCodeService
{
    private static readonly HashSet<string> AllowedPurposes = new(StringComparer.Ordinal)
    {
        "verify-email",
        "reset-password",
        "login"
    };

    private SmtpOptions Settings => liveOptions?.CurrentValue ?? smtpOptions.Value;
    public int CooldownSeconds => Settings.VerificationCodeCooldownSeconds;
    public int LifetimeMinutes => Settings.VerificationCodeLifetimeMinutes;
    private static readonly TimeSpan SendWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan StateLifetime = TimeSpan.FromHours(2);
    private const int MaxSendsPerWindow = 10;
    private const int MaxAttempts = 5;
    private const int StripeCount = 256;
    private static readonly SemaphoreSlim[] Stripes = Enumerable.Range(0, StripeCount)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    public bool IsAvailable => Settings.Enabled;

    public async Task SendAsync(string email, string purpose, string binding, CancellationToken cancellationToken = default)
    {
        EnsurePurpose(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        if (!IsAvailable)
            throw new InvalidOperationException("Email verification is not available because SMTP is disabled.");
        var settings = Settings;
        var lifetime = TimeSpan.FromMinutes(settings.VerificationCodeLifetimeMinutes);
        var cooldown = TimeSpan.FromSeconds(settings.VerificationCodeCooldownSeconds);

        var normalizedEmail = NormalizeEmail(email);
        var gate = GetStripe(normalizedEmail);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            var stateKey = StateKey(normalizedEmail);
            var state = cache.Get<EmailState>(stateKey) ?? new EmailState();
            if (state.CooldownUntil > now)
            {
                SaveState(stateKey, state);
                var seconds = (int)Math.Ceiling((state.CooldownUntil - now).TotalSeconds);
                throw new EmailCodeRequestException($"请在 {seconds} 秒后再请求验证码。", seconds);
            }

            var cacheKey = ChallengeKey(normalizedEmail, purpose, binding);
            if (cache.TryGetValue<Challenge>(cacheKey, out var existing) && existing is not null
                && existing.ExpiresAt > now && existing.FailedAttempts < MaxAttempts)
                throw new EmailCodeRequestException("存在有效的验证码，请使用已收到的验证码。", 0, true);

            while (state.Sends.TryPeek(out var sentAt) && now - sentAt >= SendWindow)
                state.Sends.Dequeue();
            if (state.Sends.Count >= MaxSendsPerWindow)
            {
                SaveState(stateKey, state);
                throw new InvalidOperationException("The email verification send limit has been reached.");
            }

            state.CooldownUntil = now + cooldown;
            state.Sends.Enqueue(now);
            SaveState(stateKey, state);
            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var salt = RandomNumberGenerator.GetBytes(16);
            var challenge = new Challenge(salt, Hash(code, salt), now + lifetime);
            cache.Set(cacheKey, challenge, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = lifetime
            });

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                var purposeLabel = purpose switch
                {
                    "verify-email" => "邮箱绑定",
                    "reset-password" => "密码找回",
                    "login" => "登录",
                    _ => throw new InvalidOperationException("Unsupported email verification purpose.")
                };
                await sender.SendAsync(
                    normalizedEmail,
                    "NexusAuth 邮箱验证码",
                    $"您好，您正在进行 NexusAuth{purposeLabel}操作。验证码：{code}，{settings.VerificationCodeLifetimeMinutes} 分钟内有效。若非本人操作，请忽略此邮件。",
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                cache.Remove(cacheKey);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public bool TryConsume(string email, string purpose, string binding, string code) =>
        TryMatch(email, purpose, binding, code, consume: true);

    /// <summary>
    /// 中文注释：校验验证码但不消费，用于还需要继续校验其他因素（例如动态码）的流程；
    /// 错误尝试与 <see cref="TryConsume"/> 共用同一失败次数上限。
    /// </summary>
    public bool TryVerify(string email, string purpose, string binding, string code) =>
        TryMatch(email, purpose, binding, code, consume: false);

    private bool TryMatch(string email, string purpose, string binding, string code, bool consume)
    {
        if (!AllowedPurposes.Contains(purpose)
            || string.IsNullOrWhiteSpace(binding)
            || !TryNormalizeEmail(email, out var normalizedEmail)
            || string.IsNullOrEmpty(code))
            return false;

        var gate = GetStripe(normalizedEmail);
        gate.Wait();
        try
        {
            var cacheKey = ChallengeKey(normalizedEmail, purpose, binding);
            if (!cache.TryGetValue<Challenge>(cacheKey, out var challenge) || challenge is null)
                return false;

            if (challenge.ExpiresAt <= timeProvider.GetUtcNow())
            {
                cache.Remove(cacheKey);
                return false;
            }

            var candidateHash = Hash(code, challenge.Salt);
            if (CryptographicOperations.FixedTimeEquals(challenge.Hash, candidateHash))
            {
                if (consume)
                    cache.Remove(cacheKey);
                return true;
            }

            challenge.FailedAttempts++;
            if (challenge.FailedAttempts >= MaxAttempts)
                cache.Remove(cacheKey);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void EnsurePurpose(string purpose)
    {
        if (!AllowedPurposes.Contains(purpose))
            throw new ArgumentException("Unsupported email verification purpose.", nameof(purpose));
    }

    private static string NormalizeEmail(string? email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var normalized = email.Trim();
        if (normalized.Length > 256
            || !MailAddress.TryCreate(normalized, out var address)
            || !string.Equals(address.Address, normalized, StringComparison.OrdinalIgnoreCase)
            || normalized.Any(char.IsControl))
            throw new ArgumentException("A single valid email address is required.", nameof(email));
        return address.Address.ToLowerInvariant();
    }

    private static bool TryNormalizeEmail(string? email, out string normalized)
    {
        try
        {
            normalized = NormalizeEmail(email);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private SemaphoreSlim GetStripe(string email)
    {
        var index = (int)((uint)StringComparer.Ordinal.GetHashCode(email) % StripeCount);
        return Stripes[index];
    }

    private static string StateKey(string email) => $"email-verification-state:{email}";

    private void SaveState(string key, EmailState state) =>
        cache.Set(key, state, new MemoryCacheEntryOptions { SlidingExpiration = StateLifetime });

    private static string ChallengeKey(string email, string purpose, string binding)
    {
        var input = Encoding.UTF8.GetBytes($"{email}\0{purpose}\0{binding}");
        return $"email-verification-challenge:{Convert.ToHexString(SHA256.HashData(input))}";
    }

    private static byte[] Hash(string code, byte[] salt)
    {
        var codeBytes = Encoding.UTF8.GetBytes(code);
        var input = new byte[salt.Length + codeBytes.Length];
        salt.CopyTo(input, 0);
        codeBytes.CopyTo(input, salt.Length);
        return SHA256.HashData(input);
    }

    private sealed class EmailState
    {
        public DateTimeOffset CooldownUntil { get; set; }
        public Queue<DateTimeOffset> Sends { get; } = new();
    }

    private sealed class Challenge(byte[] salt, byte[] hash, DateTimeOffset expiresAt)
    {
        public byte[] Salt { get; } = salt;
        public byte[] Hash { get; } = hash;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int FailedAttempts { get; set; }
    }
}
