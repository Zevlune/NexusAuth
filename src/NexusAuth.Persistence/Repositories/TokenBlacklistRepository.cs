using Luck.EntityFrameworkCore.DbContexts;
using Luck.EntityFrameworkCore.Repositories;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Persistence.Repositories;

public class TokenBlacklistRepository : EfCoreEntityRepository<TokenBlacklistEntry, Guid>, ITokenBlacklistRepository
{
    private readonly LuckDbContextBase _dbContext;
    private readonly IUnitOfWork _unitOfWork;

    public TokenBlacklistRepository(IUnitOfWork unitOfWork) : base(unitOfWork)
    {
        _unitOfWork = unitOfWork;
        _dbContext = unitOfWork.GetLuckDbContext() as LuckDbContextBase
            ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
    }

    public async Task<TokenBlacklistEntry?> FindByJtiAsync(string jti, CancellationToken ct = default)
    {
        return await FindAll(t => t.Jti == jti).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> ExistsActiveAsync(string jti, DateTimeOffset now, CancellationToken ct = default)
    {
        if (await FindAll(t => t.Jti == jti && t.ExpiresAt > now).AnyAsync(ct))
            return true;

        // A rotated refresh token's own lifetime can end while a descendant token
        // remains active. Keep replay detection effective for the whole family.
        if (!jti.StartsWith(RefreshToken.RotationMarkerPrefix, StringComparison.Ordinal))
            return false;

        if (!await FindAll(t => t.Jti == jti && t.TokenType == RefreshToken.RotationMarkerTokenType).AnyAsync(ct))
            return false;

        var rotatedToken = await _dbContext.Set<RefreshToken>()
            .AsNoTracking()
            .Where(token => jti == RefreshToken.RotationMarkerPrefix + token.TokenHash)
            .Select(token => new { token.UserId, token.ClientId })
            .FirstOrDefaultAsync(ct);
        if (rotatedToken is null)
            return false;

        return await _dbContext.Set<RefreshToken>()
            .AnyAsync(token => token.UserId == rotatedToken.UserId
                && token.ClientId == rotatedToken.ClientId
                && !token.IsRevoked
                && token.ExpiresAt > now, ct);
    }

    public async Task AddAsync(TokenBlacklistEntry entry, CancellationToken ct = default)
    {
        _dbContext.Add(entry);
        await _unitOfWork.CommitAsync(ct);
    }
}
