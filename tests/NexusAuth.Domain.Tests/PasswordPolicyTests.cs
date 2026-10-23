using System.Text;
using NexusAuth.Domain.AggregateRoots.Users;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData(null, PasswordPolicy.RequiredMessage)]
    [InlineData("", PasswordPolicy.RequiredMessage)]
    [InlineData("        ", PasswordPolicy.RequiredMessage)]
    [InlineData("a", PasswordPolicy.TooShortMessage)]
    [InlineData("Short1!", PasswordPolicy.TooShortMessage)]
    public void Rejects_missing_or_short_passwords(string? password, string expectedMessage)
    {
        Assert.Equal(expectedMessage, PasswordPolicy.GetValidationError(password));
        var exception = Assert.Throws<ArgumentException>(() => PasswordPolicy.EnsureValid(password));
        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void Accepts_eight_characters_and_exactly_72_utf8_bytes()
    {
        Assert.Null(PasswordPolicy.GetValidationError("Passw0rd"));
        Assert.Null(PasswordPolicy.GetValidationError(new string('p', 72)));

        var multiByteAtLimit = string.Concat(Enumerable.Repeat("汉", 24));
        Assert.Equal(72, Encoding.UTF8.GetByteCount(multiByteAtLimit));
        Assert.Null(PasswordPolicy.GetValidationError(multiByteAtLimit));
    }

    [Fact]
    public void Rejects_passwords_that_bcrypt_would_silently_truncate()
    {
        Assert.Equal(PasswordPolicy.TooLongMessage, PasswordPolicy.GetValidationError(new string('p', 73)));

        // 25 CJK characters are only 25 chars but 75 UTF-8 bytes.
        var multiByteOverLimit = string.Concat(Enumerable.Repeat("汉", 25));
        Assert.Equal(PasswordPolicy.TooLongMessage, PasswordPolicy.GetValidationError(multiByteOverLimit));
    }
}
