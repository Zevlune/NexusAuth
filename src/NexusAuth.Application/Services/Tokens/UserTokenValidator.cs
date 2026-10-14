using System.Globalization;

namespace NexusAuth.Application.Services.Tokens;

public sealed class UserTokenValidator(IUserRepository users, ITokenBlacklistRepository blacklist) : IUserTokenValidator
{
    public const string EpochClaim = "token_epoch";

    public async Task<bool> IsActiveAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value ?? principal.FindFirst(ClaimTypes.SerialNumber)?.Value;
        if (!string.IsNullOrWhiteSpace(jti) && await blacklist.ExistsActiveAsync(jti, DateTimeOffset.UtcNow, ct))
            return false;

        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(subject)) return false;
        if (!Guid.TryParse(subject, out var userId)) return true; // Client credentials have a client subject.

        var user = await users.FindByIdAsync(userId, ct);
        if (user is not { IsActive: true }) return false;

        var epoch = principal.FindFirst(EpochClaim)?.Value;
        if (epoch is not null)
            return long.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks == (user.TokenInvalidBefore?.UtcTicks ?? 0);

        // Compatibility with tokens issued before token_epoch was introduced.
        // Their whole-second iat must be strictly newer than a revocation.
        if (!user.TokenInvalidBefore.HasValue) return true;
        var issuedAt = principal.FindFirst(JwtRegisteredClaimNames.Iat)?.Value;
        if (!long.TryParse(issuedAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) return false;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds) > user.TokenInvalidBefore.Value; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
