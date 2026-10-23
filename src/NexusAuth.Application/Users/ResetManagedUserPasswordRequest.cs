using System.ComponentModel.DataAnnotations;

namespace NexusAuth.Application.Users;

public sealed record ResetManagedUserPasswordRequest([Required, StringLength(PasswordPolicy.MaximumUtf8Bytes, MinimumLength = PasswordPolicy.MinimumLength)] string NewPassword);
