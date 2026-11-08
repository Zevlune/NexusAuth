using NexusAuth.Application.Users;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class SecondFactorCodeTests
{
    [Theory]
    [InlineData("123456", "123456", true)]
    [InlineData("123 456", "123456", true)]
    [InlineData(" 123456 \t", "123456", true)]
    [InlineData("A1B2 C3D4", "A1B2C3D4", false)]
    [InlineData("12345", "12345", false)]
    [InlineData(null, "", false)]
    public void Normalize_strips_whitespace_before_choosing_the_factor(string? input, string expected, bool isTotp)
    {
        var normalized = SecondFactorCode.Normalize(input);

        Assert.Equal(expected, normalized);
        Assert.Equal(isTotp, SecondFactorCode.IsTotp(normalized));
    }
}
