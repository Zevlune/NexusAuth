using System.ComponentModel.DataAnnotations;

namespace NexusAuth.Application.Users;

public sealed record ResetManagedUserPasswordRequest([Required, StringLength(72)] string NewPassword);
