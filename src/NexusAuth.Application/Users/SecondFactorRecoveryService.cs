using Luck.Logging.Serilog;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Sessions;

namespace NexusAuth.Application.Users;

public sealed class SecondFactorRecoveryService(
    IUserRepository userRepository,
    IUserCredentialRepository credentialRepository,
    IWebAuthnCredentialRepository passkeyRepository,
    ISecondFactorRecoveryRepository recoveryRepository,
    IRefreshTokenRepository refreshTokenRepository,
    ISsoSessionService sessionService,
    IOptions<NexusAuthSecurityOptions> securityOptions,
    ILogger<SecondFactorRecoveryService> logger) : ISecondFactorRecoveryService
{
    private const int RecoveryCodeCount = 10;
    private const int RecoveryCodeEntropyBytes = 20;

    private readonly NexusAuthSecurityOptions security = securityOptions.Value;

    public async Task<IReadOnlyList<string>> GenerateCodesAsync(Guid userId, CancellationToken ct = default)
    {
        _ = await userRepository.FindByIdAsync(userId, ct)
            ?? throw new KeyNotFoundException("用户不存在。");
        await using var attempt = await userRepository.BeginAuthenticationAttemptAsync(userId, ct)
            ?? throw new InvalidOperationException("The account is busy with another authentication operation.");

        var clearTextCodes = new List<string>(RecoveryCodeCount);
        await recoveryRepository.ExecuteInTransactionAsync(async transactionCt =>
        {
            var user = attempt.User;
            if (!user.IsActive)
                throw new InvalidOperationException("Inactive users cannot generate recovery codes.");

            if (await credentialRepository.GetEnabledTotpAsync(userId, transactionCt) is not { Count: > 0 })
                throw new InvalidOperationException("An enabled TOTP authenticator is required before recovery codes can be generated.");

            clearTextCodes.AddRange(Enumerable.Range(0, RecoveryCodeCount)
                .Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(RecoveryCodeEntropyBytes))));
            var now = DateTimeOffset.UtcNow;
            var entities = clearTextCodes
                .Select(code => UserRecoveryCode.Create(userId, code, now))
                .ToArray();
            await recoveryRepository.ReplaceAsync(userId, entities, transactionCt);
        }, ct);

        // Deliberately do not log the clear-text values. They are returned only
        // to the caller that completed this generation operation.
        logger.LogLuckInformation(
            "Second-factor recovery codes generated. UserId={UserId} Count={Count}",
            [userId, clearTextCodes.Count]);
        return clearTextCodes;
    }

    public async Task<bool> RedeemAsync(Guid userId, string? code, CancellationToken ct = default)
    {
        // The advisory lock is deliberately held for the complete check,
        // consume and failure-counter update. This prevents two concurrent
        // guesses from both observing the same account state.
        await using var attempt = await userRepository.BeginAuthenticationAttemptAsync(userId, ct);
        if (attempt is null)
            return false;

        var user = attempt.User;
        var now = DateTimeOffset.UtcNow;
        if (!user.IsActive || user.IsLoginLocked(now))
            return false;

        var normalizedCode = code?.Trim();
        var redeemed = !string.IsNullOrWhiteSpace(normalizedCode)
            && await recoveryRepository.TryRedeemAsync(
                userId,
                UserRecoveryCode.Hash(normalizedCode!),
                now,
                ct);
        if (redeemed)
            return true;

        await userRepository.RegisterFailedLoginAsync(
            userId,
            Math.Max(1, security.LoginFailureLimit),
            TimeSpan.FromMinutes(Math.Max(1, security.LoginLockoutMinutes)),
            now,
            ct);
        return false;
    }

    public async Task ResetAsync(Guid userId, Guid actorId, string reason, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(actorId, Guid.Empty);
        _ = await userRepository.FindByIdAsync(userId, ct)
            ?? throw new KeyNotFoundException("用户不存在。");
        await using var attempt = await userRepository.BeginAuthenticationAttemptAsync(userId, ct)
            ?? throw new InvalidOperationException("The account is busy with another authentication operation.");
        await recoveryRepository.ExecuteInTransactionAsync(async transactionCt =>
        {
            var user = attempt.User;
            var now = DateTimeOffset.UtcNow;
            var audit = SecondFactorRecoveryAudit.Create(actorId, userId, reason, now);

            // These operations intentionally leave IsActive untouched. A
            // disabled account must never be re-enabled by a recovery reset.
            await credentialRepository.DisableAllTotpAsync(userId, now, transactionCt);
            await passkeyRepository.DisableAllForUserAsync(userId, now, transactionCt);
            await recoveryRepository.DeleteForUserAsync(userId, transactionCt);

            user.InvalidateTokens(now);
            await userRepository.UpdateAsync(user, transactionCt);
            await refreshTokenRepository.RevokeAllForUserAsync(userId, transactionCt);
            await sessionService.RevokeAllForUserAsync(userId, transactionCt);

            await recoveryRepository.AddAuditAsync(audit, transactionCt);
        }, ct);

        logger.LogLuckInformation(
            "Second factors reset by an administrator. UserId={UserId} ActorId={ActorId}",
            [userId, actorId]);
    }

}
