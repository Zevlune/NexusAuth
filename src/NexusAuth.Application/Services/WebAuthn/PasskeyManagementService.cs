using NexusAuth.Application.Services.Sessions;

namespace NexusAuth.Application.Services.WebAuthn;

public sealed class PasskeyManagementService(
    IWebAuthnCredentialRepository credentials,
    IUserRepository users,
    IRefreshTokenRepository tokens,
    ISsoSessionService sessions,
    ILogger<PasskeyManagementService> logger) : IPasskeyManagementService
{
    public async Task<IReadOnlyList<WebAuthnCredentialSummary>> GetAsync(Guid userId, CancellationToken ct = default) =>
        (await credentials.GetForUserAsync(userId, ct)).Select(credential => new WebAuthnCredentialSummary(
            credential.Id, credential.DisplayName, credential.CreatedAt, credential.LastUsedAt,
            credential.DisabledAt, credential.IsBackupEligible, credential.IsBackedUp)).ToArray();

    public async Task<bool> RenameAsync(Guid userId, Guid id, string displayName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var name = displayName.Trim();
        if (name.Length > 128)
            throw new ArgumentException("Passkey 名称不能超过 128 个字符。", nameof(displayName));
        var changed = await credentials.RenameAsync(userId, id, name, ct);
        if (changed)
            logger.LogInformation("Passkey renamed. UserId={UserId} CredentialId={CredentialId}", userId, id);
        return changed;
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid id, Guid actorId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        reason = reason.Trim();
        if (reason.Length > 500 || reason.Any(char.IsControl))
            throw new ArgumentException("撤销原因不能超过 500 个字符或包含控制字符。", nameof(reason));
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null || !await credentials.DisableAsync(userId, id, DateTimeOffset.UtcNow, ct))
            return false;

        logger.LogWarning("Passkey revoked. UserId={UserId} CredentialId={CredentialId} ActorId={ActorId} Reason={Reason}",
            userId, id, actorId, reason);
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        await users.UpdateAsync(user, ct);
        await tokens.RevokeAllForUserAsync(userId, ct);
        await sessions.RevokeAllForUserAsync(userId, ct);
        return true;
    }
}
