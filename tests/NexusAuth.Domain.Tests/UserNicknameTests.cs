using NexusAuth.Domain.AggregateRoots.Users;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class UserNicknameTests
{
    [Fact]
    public void Updating_nickname_trims_input_and_preserves_other_profile_fields()
    {
        var user = User.Create("test-user", "test-password", "微信用户", "user@example.test", "13800138000");
        var passwordHash = user.PasswordHash;
        var createdAt = user.CreatedAt;
        var beforeUpdate = DateTimeOffset.UtcNow;

        user.UpdateNickname("  小明  ");

        Assert.Equal("小明", user.Nickname);
        Assert.Equal("test-user", user.Username);
        Assert.Equal("user@example.test", user.Email);
        Assert.Equal("13800138000", user.PhoneNumber);
        Assert.Equal(passwordHash, user.PasswordHash);
        Assert.Equal(createdAt, user.CreatedAt);
        Assert.True(user.UpdatedAt >= beforeUpdate);
        Assert.True(user.IsActive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_nickname_is_rejected_without_mutating_user(string? nickname)
    {
        var user = User.Create("test-user", "test-password", "原昵称");
        var updatedAt = user.UpdatedAt;

        Assert.ThrowsAny<ArgumentException>(() => user.UpdateNickname(nickname!));

        Assert.Equal("原昵称", user.Nickname);
        Assert.Equal(updatedAt, user.UpdatedAt);
    }

    [Fact]
    public void Nickname_enforces_database_length_limit()
    {
        var user = User.Create("test-user", "test-password", "原昵称");
        Assert.Throws<ArgumentException>(() => user.UpdateNickname(new string('字', 101)));
        Assert.Equal("原昵称", user.Nickname);
        user.UpdateNickname(new string('字', 100));
        Assert.Equal(100, user.Nickname.Length);
    }

    [Fact]
    public void Nickname_length_counts_emoji_as_single_unicode_characters()
    {
        var user = User.Create("test-user", "test-password", "原昵称");
        var nickname = string.Concat(Enumerable.Repeat("😀", 100));
        user.UpdateNickname(nickname);
        Assert.Equal(nickname, user.Nickname);
        Assert.Throws<ArgumentException>(() => user.UpdateNickname(nickname + "😀"));
    }

    [Fact]
    public void System_account_nickname_cannot_be_changed()
    {
        var user = User.Create("admin", "test-password", "管理员");
        user.MarkAsSystemAccount();

        Assert.Throws<InvalidOperationException>(() => user.UpdateNickname("新昵称"));
        Assert.Equal("管理员", user.Nickname);
    }
}
