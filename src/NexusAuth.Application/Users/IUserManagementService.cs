using System.ComponentModel.DataAnnotations;
using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Application.Users;

public interface IUserManagementService : IScopedDependency
{
    Task<PagedResult<ManagedUserDto>> GetPagedAsync(string? keyword, bool? isActive, int page, int pageSize, CancellationToken ct = default);
    Task<ManagedUserDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ManagedUserDto> CreateAsync(CreateManagedUserRequest request, CancellationToken ct = default);
    Task<ManagedUserDto> UpdateProfileAsync(Guid id, UpdateManagedUserRequest request, CancellationToken ct = default);
    Task<ManagedUserDto> UpdateStatusAsync(Guid id, bool isActive, CancellationToken ct = default);
    Task ResetPasswordAsync(Guid id, ResetManagedUserPasswordRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task RevokeTokensAsync(Guid id, CancellationToken ct = default);
}

public sealed record ManagedUserDto(
    Guid Id, string Username, string Nickname, string? Email, string? PhoneNumber, bool IsActive,
    string? ExternalId, string? GivenName, string? FamilyName, string? Title, string? UserType,
    string? PreferredLanguage, string? Locale, string? Timezone, bool IsSystemAccount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record UpdateManagedUserRequest(
    [Required, StringLength(100)] string Nickname,
    [EmailAddress, StringLength(256)] string? Email,
    [StringLength(20)] string? PhoneNumber,
    [StringLength(100)] string? GivenName,
    [StringLength(100)] string? FamilyName,
    [StringLength(256)] string? Title,
    [StringLength(100)] string? UserType,
    [StringLength(35)] string? PreferredLanguage,
    [StringLength(35)] string? Locale,
    [StringLength(100)] string? Timezone);

public sealed record CreateManagedUserRequest(
    [Required, StringLength(100)] string Username,
    [Required, StringLength(72)] string Password,
    [Required, StringLength(100)] string Nickname,
    [EmailAddress, StringLength(256)] string? Email = null,
    [StringLength(20)] string? PhoneNumber = null,
    [StringLength(100)] string? GivenName = null,
    [StringLength(100)] string? FamilyName = null,
    [StringLength(256)] string? Title = null,
    [StringLength(100)] string? UserType = null,
    [StringLength(35)] string? PreferredLanguage = null,
    [StringLength(35)] string? Locale = null,
    [StringLength(100)] string? Timezone = null,
    bool IsActive = true);

public sealed record ResetManagedUserPasswordRequest([Required, StringLength(72)] string NewPassword);
