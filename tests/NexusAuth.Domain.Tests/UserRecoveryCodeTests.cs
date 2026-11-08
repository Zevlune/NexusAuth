using NexusAuth.Domain.Entities;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class UserRecoveryCodeTests
{
    [Fact]
    public void Hash_ignores_case_and_surrounding_whitespace()
    {
        const string code = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";

        Assert.Equal(UserRecoveryCode.Hash(code), UserRecoveryCode.Hash(code.ToLowerInvariant()));
        Assert.Equal(UserRecoveryCode.Hash(code), UserRecoveryCode.Hash($"  {code}\n"));
        Assert.Equal(UserRecoveryCode.Create(Guid.NewGuid(), code).CodeHash, UserRecoveryCode.Hash(code.ToLowerInvariant()));
    }
}
