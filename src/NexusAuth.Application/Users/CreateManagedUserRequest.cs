using System.ComponentModel.DataAnnotations;

namespace NexusAuth.Application.Users;

public sealed record CreateManagedUserRequest(
    [Required, StringLength(100)] string Username,
    [Required, StringLength(PasswordPolicy.MaximumUtf8Bytes, MinimumLength = PasswordPolicy.MinimumLength)] string Password,
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
