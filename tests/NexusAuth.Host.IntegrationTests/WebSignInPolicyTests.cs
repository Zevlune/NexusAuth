using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Host.Authentication;
using Xunit;
using Proxy = NexusAuth.Host.IntegrationTests.PasskeyManagementTests.RecordingProxy;

namespace NexusAuth.Host.IntegrationTests;

public sealed class WebSignInPolicyTests
{
    [Theory]
    [InlineData("pwd", false)]
    [InlineData("webauthn", false)]
    [InlineData("webauthn uv", true)]
    [InlineData("pwd otp", true)]
    [InlineData("pwd nototp", false)]
    public async Task Required_factor_is_enforced_at_session_issuance(string methods, bool allowed)
    {
        var signedIn = false;
        using var services = new ServiceCollection().AddSingleton(Proxy.Create<IAuthenticationService>((method, _) =>
        {
            if (method.Name == nameof(IAuthenticationService.SignInAsync)) signedIn = true;
            return null;
        })).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var users = Proxy.Create<IUserRepository>((method, _) => method.Name switch
        {
            nameof(IUserRepository.TryResetLoginFailuresAsync) => true,
            _ => throw new NotSupportedException(method.Name),
        });
        var sessions = Proxy.Create<ISsoSessionService>((method, _) => method.Name switch
        {
            nameof(ISsoSessionService.CreateAsync) => Guid.NewGuid(),
            _ => throw new NotSupportedException(method.Name),
        });
        var options = new LoginFlowOptions { Steps = [new() { Type = "totp", Requirement = "required" }] };
        var service = new WebSignInService(sessions, users, Options.Create(options));
        var user = User.Create("alice", "Only-a-test-password!42", "Alice");
        if (allowed)
            await service.SignInAsync(context, user, false, DateTimeOffset.UtcNow, methods);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignInAsync(context, user, false, DateTimeOffset.UtcNow, methods));
        Assert.Equal(allowed, signedIn);
    }

    [Fact]
    public async Task Concurrent_security_change_prevents_issuing_a_session()
    {
        var users = Proxy.Create<IUserRepository>((method, _) => method.Name == nameof(IUserRepository.TryResetLoginFailuresAsync)
            ? false : throw new NotSupportedException(method.Name));
        var sessions = Proxy.Create<ISsoSessionService>((_, _) => throw new InvalidOperationException("Must not create a session."));
        var service = new WebSignInService(sessions, users, Options.Create(new LoginFlowOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignInAsync(new DefaultHttpContext(),
            User.Create("alice", "Only-a-test-password!42", "Alice"), false, DateTimeOffset.UtcNow, "pwd"));
    }
}
