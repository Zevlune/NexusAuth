namespace NexusAuth.Application.Users;

using Luck.Logging.Serilog;
using System.ComponentModel.DataAnnotations;
using NexusAuth.Application.Services.Sessions;

public class UserManagementService(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    ISsoSessionService sessionService,
    ILogger<UserManagementService> logger) : IUserManagementService
{
    public async Task<PagedResult<ManagedUserDto>> GetPagedAsync(string? keyword, bool? isActive, int page, int pageSize, CancellationToken ct = default)
    {
        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = pageSize <= 0 ? 10 : Math.Min(pageSize, 100);
        var (items, total) = await userRepository.GetAdminPagedAsync(keyword, isActive, normalizedPage, normalizedPageSize, ct);
        return new PagedResult<ManagedUserDto>(items.Select(Map).ToList(), total, normalizedPage, normalizedPageSize);
    }

    public async Task<ManagedUserDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var user = await userRepository.FindByIdAsync(id, ct);
        return user is null ? null : Map(user);
    }

    public async Task<ManagedUserDto> CreateAsync(CreateManagedUserRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Password);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Nickname);
        if (Encoding.UTF8.GetByteCount(request.Password) > 72)
            throw new ArgumentException("密码不能超过 72 个 UTF-8 字节。", nameof(request.Password));

        var username = request.Username.Trim();
        if (await userRepository.FindByUsernameAsync(username, ct) is not null)
            throw new InvalidOperationException("用户名已存在。");
        await EnsureEmailUniqueAsync(request.Email, Guid.Empty, ct);
        await EnsurePhoneUniqueAsync(request.PhoneNumber, Guid.Empty, ct);

        var user = User.Create(username, request.Password, request.Nickname.Trim());
        user.UpdateScimProfile(username, request.Nickname, request.IsActive, email: request.Email,
            phoneNumber: request.PhoneNumber, givenName: request.GivenName, familyName: request.FamilyName,
            title: request.Title, userType: request.UserType, preferredLanguage: request.PreferredLanguage,
            locale: request.Locale, timezone: request.Timezone);
        await userRepository.AddAsync(user, ct);
        logger.LogLuckInformation("User created by an administrator. UserId={UserId}", [user.Id]);
        return Map(user);
    }

    public async Task<ManagedUserDto> UpdateProfileAsync(Guid id, UpdateManagedUserRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Nickname);
        var user = await FindManagedUserAsync(id, ct);
        if (user.IsSystemAccount) throw new InvalidOperationException("System accounts cannot be modified.");
        await EnsureEmailUniqueAsync(request.Email, user.Id, ct);
        await EnsurePhoneUniqueAsync(request.PhoneNumber, user.Id, ct);
        user.UpdateScimProfile(user.Username, request.Nickname, user.IsActive, user.ExternalId, request.Email, request.PhoneNumber,
            request.GivenName, request.FamilyName, user.MiddleName, user.HonorificPrefix, user.HonorificSuffix, user.ProfileUrl,
            request.Title, request.UserType, request.PreferredLanguage, request.Locale, request.Timezone);
        await userRepository.UpdateAsync(user, ct);
        return Map(user);
    }

    public async Task<ManagedUserDto> UpdateStatusAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        var user = await FindManagedUserAsync(id, ct);
        if (user.IsSystemAccount) throw new InvalidOperationException("System accounts cannot be modified.");
        user.UpdateScimProfile(user.Username, user.Nickname, isActive, user.ExternalId, user.Email, user.PhoneNumber,
            user.GivenName, user.FamilyName, user.MiddleName, user.HonorificPrefix, user.HonorificSuffix, user.ProfileUrl,
            user.Title, user.UserType, user.PreferredLanguage, user.Locale, user.Timezone);
        if (!isActive)
            user.InvalidateTokens(DateTimeOffset.UtcNow);
        await userRepository.UpdateAsync(user, ct);
        if (!isActive)
        {
            await refreshTokenRepository.RevokeAllForUserAsync(user.Id, ct);
            await sessionService.RevokeAllForUserAsync(user.Id, ct);
        }
        return Map(user);
    }

    public async Task ResetPasswordAsync(Guid id, ResetManagedUserPasswordRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NewPassword);
        if (Encoding.UTF8.GetByteCount(request.NewPassword) > 72)
            throw new ArgumentException("密码不能超过 72 个 UTF-8 字节。", nameof(request.NewPassword));

        var user = await FindManagedUserAsync(id, ct);
        if (user.IsSystemAccount)
            throw new InvalidOperationException("System account passwords cannot be reset.");

        user.ChangePassword(request.NewPassword);
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        await userRepository.UpdateAsync(user, ct);
        await refreshTokenRepository.RevokeAllForUserAsync(user.Id, ct);
        await sessionService.RevokeAllForUserAsync(user.Id, ct);

        logger.LogLuckInformation(
            "User password reset by an administrator. UserId={UserId} Outcome={Outcome}",
            [user.Id, "PasswordResetByAdministrator"]);
    }

    public async Task RevokeTokensAsync(Guid id, CancellationToken ct = default)
    {
        var user = await FindManagedUserAsync(id, ct);
        if (user.IsSystemAccount) throw new InvalidOperationException("System account tokens cannot be revoked.");
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        await userRepository.UpdateAsync(user, ct);
        await refreshTokenRepository.RevokeAllForUserAsync(id, ct);
        await sessionService.RevokeAllForUserAsync(id, ct);
        logger.LogLuckInformation("User tokens revoked by an administrator. UserId={UserId}", [id]);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var user = await FindManagedUserAsync(id, ct);
        if (user.IsSystemAccount) throw new InvalidOperationException("System accounts cannot be deleted.");

        // Persist the denial first so a partial failure cannot leave the account usable.
        user.UpdateScimProfile(user.Username, user.Nickname, false, user.ExternalId, user.Email, user.PhoneNumber,
            user.GivenName, user.FamilyName, user.MiddleName, user.HonorificPrefix, user.HonorificSuffix, user.ProfileUrl,
            user.Title, user.UserType, user.PreferredLanguage, user.Locale, user.Timezone);
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        await userRepository.UpdateAsync(user, ct);
        await refreshTokenRepository.RevokeAllForUserAsync(id, ct);
        await sessionService.RevokeAllForUserAsync(id, ct);
        await userRepository.DeleteAsync(user, ct);
        logger.LogLuckInformation("User deleted by an administrator. UserId={UserId}", [id]);
    }

    private async Task<User> FindManagedUserAsync(Guid id, CancellationToken ct) =>
        await userRepository.FindByIdAsync(id, ct) ?? throw new KeyNotFoundException("用户不存在。");

    private static void ValidateRequest(object request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
            throw new ArgumentException(string.Join(" ", results.Select(result => result.ErrorMessage)));
    }

    private async Task EnsureEmailUniqueAsync(string? email, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        var existing = await userRepository.FindByEmailAsync(email.Trim().ToLowerInvariant(), ct);
        if (existing is not null && existing.Id != userId) throw new InvalidOperationException("Email is already used by another user.");
    }

    private async Task EnsurePhoneUniqueAsync(string? phoneNumber, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber)) return;
        var existing = await userRepository.FindByPhoneNumberAsync(phoneNumber.Trim(), ct);
        if (existing is not null && existing.Id != userId) throw new InvalidOperationException("手机号已被其他用户使用。");
    }

    private static ManagedUserDto Map(User user) => new(user.Id, user.Username, user.Nickname, user.Email, user.PhoneNumber,
        user.IsActive, user.ExternalId, user.GivenName, user.FamilyName, user.Title, user.UserType, user.PreferredLanguage,
        user.Locale, user.Timezone, user.IsSystemAccount, user.CreatedAt, user.UpdatedAt);
}
