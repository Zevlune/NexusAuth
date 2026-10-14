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
    [property: Required, StringLength(100)] string Nickname,
    [property: EmailAddress, StringLength(256)] string? Email,
    [property: StringLength(20)] string? PhoneNumber,
    [property: StringLength(100)] string? GivenName,
    [property: StringLength(100)] string? FamilyName,
    [property: StringLength(256)] string? Title,
    [property: StringLength(100)] string? UserType,
    [property: StringLength(35)] string? PreferredLanguage,
    [property: StringLength(35)] string? Locale,
    [property: StringLength(100)] string? Timezone);

public sealed record CreateManagedUserRequest(
    [property: Required, StringLength(100)] string Username,
    [property: Required, StringLength(72)] string Password,
    [property: Required, StringLength(100)] string Nickname,
    [property: EmailAddress, StringLength(256)] string? Email = null,
    [property: StringLength(20)] string? PhoneNumber = null,
    [property: StringLength(100)] string? GivenName = null,
    [property: StringLength(100)] string? FamilyName = null,
    [property: StringLength(256)] string? Title = null,
    [property: StringLength(100)] string? UserType = null,
    [property: StringLength(35)] string? PreferredLanguage = null,
    [property: StringLength(35)] string? Locale = null,
    [property: StringLength(100)] string? Timezone = null,
    bool IsActive = true);

public sealed record ResetManagedUserPasswordRequest([property: Required, StringLength(72)] string NewPassword);
