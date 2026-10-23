using System.ComponentModel.DataAnnotations;

namespace NexusAuth.Application.Users;

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
