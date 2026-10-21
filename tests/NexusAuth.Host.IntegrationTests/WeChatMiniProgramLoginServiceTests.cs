using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class WeChatMiniProgramLoginServiceTests
{
    private const string AppId = "wx-app-123";
    private const string AppSecret = "app-secret";
    private const string Code2SessionEndpoint = "https://wechat.example.test/sns/jscode2session";
    private const string SessionKeySecret = "session-key-that-must-not-leak";

    [Fact]
    public async Task First_login_creates_a_local_user_and_binds_openid_as_external_subject()
    {
        const string openId = "openid-first";
        const string unionId = "union-first";
        using var harness = CreateHarness(JsonResponse(
            $$"""
            {
              "openid": "{{openId}}",
              "session_key": "{{SessionKeySecret}}",
              "unionid": "{{unionId}}"
            }
            """));

        var result = await harness.Service.AuthenticateAsync("  login-code  ");

        var createCall = Assert.Single(harness.GetOrCreateCalls);
        Assert.Same(createCall.User, result.User);
        Assert.Equal(createCall.User.Id, createCall.Identity.UserId);
        Assert.StartsWith("wx_", createCall.User.Username);
        Assert.True(createCall.User.IsActive);
        Assert.Equal(WeChatMiniProgramLoginService.Provider, createCall.Identity.Provider);
        Assert.Equal(AppId, createCall.Identity.Issuer);
        Assert.Equal(openId, createCall.Identity.Subject);
        Assert.Equal(unionId, createCall.Identity.UnionId);
        Assert.NotEqual(openId, createCall.User.Id.ToString());

        Assert.Equal(WeChatMiniProgramLoginService.Provider, result.Provider);
        Assert.Equal(AppId, result.Issuer);
        Assert.Equal(openId, result.Subject);
        Assert.Equal(unionId, result.UnionId);
        Assert.Single(harness.Users);
        Assert.Single(harness.Identities);

        var requestUri = Assert.Single(harness.HttpHandler.RequestUris);
        Assert.Contains($"appid={AppId}", requestUri.Query);
        Assert.Contains("js_code=login-code", requestUri.Query);
        Assert.DoesNotContain(SessionKeySecret, requestUri.OriginalString);
        Assert.DoesNotContain(SessionKeySecret, result.ToString());
        Assert.DoesNotContain(SessionKeySecret, harness.Logger.Combined);
    }

    [Fact]
    public async Task Existing_identity_login_refreshes_timestamp_and_keeps_the_same_user()
    {
        const string openId = "openid-existing";
        var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
        var user = CreateUser("existing-user");
        var identity = UserExternalIdentity.Create(
            user.Id,
            WeChatMiniProgramLoginService.Provider,
            AppId,
            openId,
            "union-existing",
            createdAt);
        using var harness = CreateHarness(
            JsonResponse($$"""{"openid":"{{openId}}","unionid":"union-existing"}"""))
            .Link(user, identity);

        var result = await harness.Service.AuthenticateAsync("existing-code");

        Assert.Same(user, result.User);
        Assert.Same(identity, Assert.Single(harness.Identities));
        Assert.Single(harness.Users);
        Assert.Empty(harness.GetOrCreateCalls);
        Assert.Equal(1, harness.IdentityUpdateCount);
        Assert.True(identity.LastAuthenticatedAt > createdAt);
        Assert.Equal(openId, result.Subject);
    }

    [Fact]
    public async Task Disabled_linked_user_is_rejected_without_refreshing_identity()
    {
        const string openId = "openid-disabled";
        var user = CreateUser("disabled-user");
        user.UpdateScimProfile(user.Username, user.Nickname, isActive: false);
        var identity = UserExternalIdentity.Create(
            user.Id,
            WeChatMiniProgramLoginService.Provider,
            AppId,
            openId);
        using var harness = CreateHarness(
            JsonResponse($$"""{"openid":"{{openId}}"}"""))
            .Link(user, identity);

        var exception = await Assert.ThrowsAsync<WeChatMiniProgramLoginException>(
            () => harness.Service.AuthenticateAsync("disabled-code"));

        Assert.Equal("invalid_grant", exception.ErrorCode);
        Assert.Equal(0, harness.IdentityUpdateCount);
        Assert.Empty(harness.GetOrCreateCalls);
    }

    [Fact]
    public async Task Same_openid_under_another_appid_creates_an_isolated_identity()
    {
        const string openId = "shared-openid";
        const string otherAppId = "wx-other-app";
        var otherUser = CreateUser("other-app-user");
        var otherIdentity = UserExternalIdentity.Create(
            otherUser.Id,
            WeChatMiniProgramLoginService.Provider,
            otherAppId,
            openId);
        using var harness = CreateHarness(
            JsonResponse($$"""{"openid":"{{openId}}"}"""))
            .Link(otherUser, otherIdentity);

        var result = await harness.Service.AuthenticateAsync("isolated-code");

        var createCall = Assert.Single(harness.GetOrCreateCalls);
        Assert.NotSame(otherUser, result.User);
        Assert.Equal(AppId, createCall.Identity.Issuer);
        Assert.Equal(openId, createCall.Identity.Subject);
        Assert.Equal(2, harness.Users.Count);
        Assert.Equal(2, harness.Identities.Count);
        Assert.Equal(AppId, harness.LastFindCall!.Issuer);
        Assert.Equal(openId, harness.LastFindCall.Subject);
    }

    [Theory]
    [InlineData(40029)]
    [InlineData(40163)]
    public async Task Expired_or_reused_code_is_invalid_grant_without_leaking_session_key(int errorCode)
    {
        using var harness = CreateHarness(JsonResponse(
            $$"""
            {
              "errcode": {{errorCode}},
              "errmsg": "code rejected",
              "session_key": "{{SessionKeySecret}}"
            }
            """));

        var exception = await Assert.ThrowsAsync<WeChatMiniProgramLoginException>(
            () => harness.Service.AuthenticateAsync("rejected-code"));

        Assert.Equal("invalid_grant", exception.ErrorCode);
        Assert.Empty(harness.FindBySubjectCalls);
        Assert.Empty(harness.GetOrCreateCalls);
        Assert.DoesNotContain(SessionKeySecret, exception.ToString());
        Assert.DoesNotContain(SessionKeySecret, harness.Logger.Combined);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    [InlineData("missing-openid")]
    [InlineData("network")]
    public async Task Upstream_failures_are_temporarily_unavailable(string failure)
    {
        using var harness = CreateHarness(CreateFailureHandler(failure));

        var exception = await Assert.ThrowsAsync<WeChatMiniProgramLoginException>(
            () => harness.Service.AuthenticateAsync("failure-code"));

        Assert.Equal("temporarily_unavailable", exception.ErrorCode);
        Assert.Empty(harness.FindBySubjectCalls);
        Assert.Empty(harness.GetOrCreateCalls);
        Assert.DoesNotContain(SessionKeySecret, exception.ToString());
        Assert.DoesNotContain(SessionKeySecret, harness.Logger.Combined);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_does_not_touch_repositories()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = CreateHarness(new StubHttpMessageHandler(
            async (_, cancellationToken) =>
            {
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
        using var cancellationSource = new CancellationTokenSource();

        var authentication = harness.Service.AuthenticateAsync("cancel-code", cancellationSource.Token);
        await requestStarted.Task;
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authentication);
        Assert.Empty(harness.FindBySubjectCalls);
        Assert.Empty(harness.GetOrCreateCalls);
    }

    private static Harness CreateHarness(HttpMessageHandler handler) => new(handler);

    private static Harness CreateHarness(HttpResponseMessage response) =>
        CreateHarness(new StubHttpMessageHandler((_, _) => Task.FromResult(response)));

    private static StubHttpMessageHandler CreateFailureHandler(string failure) =>
        failure switch
        {
            "http" => new StubHttpMessageHandler((_, _) =>
                Task.FromResult(JsonResponse("""{"openid":"ignored"}""", HttpStatusCode.BadGateway))),
            "json" => new StubHttpMessageHandler((_, _) =>
                Task.FromResult(JsonResponse("""{"openid":"""))),
            "missing-openid" => new StubHttpMessageHandler((_, _) =>
                Task.FromResult(JsonResponse(
                    $$"""{"session_key":"{{SessionKeySecret}}","unionid":"missing-openid"}"""))),
            "network" => new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("network unavailable"))),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown failure kind."),
        };

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static User CreateUser(string username) =>
        User.Create(username, "test-password", username);

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient client;
        private readonly Dictionary<Guid, User> users = [];
        private readonly List<UserExternalIdentity> identities = [];

        public Harness(HttpMessageHandler handler)
        {
            HttpHandler = Assert.IsType<StubHttpMessageHandler>(handler);
            client = new HttpClient(handler, disposeHandler: false);
            var userRepository = RepositoryProxy.Create<IUserRepository>(InvokeUserRepository);
            var identityRepository = RepositoryProxy.Create<IUserExternalIdentityRepository>(
                InvokeIdentityRepository);

            Service = new WeChatMiniProgramLoginService(
                new SingleClientFactory(client),
                userRepository,
                identityRepository,
                Options.Create(new WeChatMiniProgramOptions
                {
                    Enabled = true,
                    AppId = AppId,
                    AppSecret = AppSecret,
                    Code2SessionEndpoint = Code2SessionEndpoint,
                }),
                Logger);
        }

        public WeChatMiniProgramLoginService Service { get; }
        public CollectingLogger<WeChatMiniProgramLoginService> Logger { get; } = new();
        public StubHttpMessageHandler HttpHandler { get; }
        public IReadOnlyCollection<User> Users => users.Values;
        public IReadOnlyList<UserExternalIdentity> Identities => identities;
        public List<FindBySubjectCall> FindBySubjectCalls { get; } = [];
        public List<GetOrCreateCall> GetOrCreateCalls { get; } = [];
        public int IdentityUpdateCount { get; private set; }
        public FindBySubjectCall? LastFindCall => FindBySubjectCalls.LastOrDefault();

        public Harness Link(User user, UserExternalIdentity identity)
        {
            Assert.Equal(user.Id, identity.UserId);
            users.Add(user.Id, user);
            identities.Add(identity);
            return this;
        }

        public void Dispose()
        {
            client.Dispose();
            HttpHandler.Dispose();
        }

        private object? InvokeUserRepository(MethodInfo method, object?[]? arguments)
        {
            if (method.Name == nameof(IUserRepository.FindByIdAsync))
                return Task.FromResult(users.GetValueOrDefault((Guid)arguments![0]!));
            return null;
        }

        private object? InvokeIdentityRepository(MethodInfo method, object?[]? arguments)
        {
            if (method.Name == nameof(IUserExternalIdentityRepository.FindBySubjectAsync))
            {
                var call = new FindBySubjectCall(
                    (string)arguments![0]!,
                    (string)arguments[1]!,
                    (string)arguments[2]!);
                FindBySubjectCalls.Add(call);
                var identity = identities.FirstOrDefault(value =>
                    value.Provider == call.Provider
                    && value.Issuer == call.Issuer
                    && value.Subject == call.Subject);
                return Task.FromResult(identity);
            }

            if (method.Name == nameof(IUserExternalIdentityRepository.GetOrCreateUserAsync))
            {
                var candidate = (User)arguments![0]!;
                var identity = (UserExternalIdentity)arguments[1]!;
                GetOrCreateCalls.Add(new GetOrCreateCall(candidate, identity));
                users.Add(candidate.Id, candidate);
                identities.Add(identity);
                return Task.FromResult(candidate);
            }

            if (method.Name == nameof(IUserExternalIdentityRepository.UpdateAsync))
            {
                IdentityUpdateCount++;
                return Task.CompletedTask;
            }

            return null;
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(WeChatMiniProgramLoginService.HttpClientName, name);
            return client;
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return responder(request, cancellationToken);
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        private readonly List<string> entries = [];

        public string Combined => string.Join(Environment.NewLine, entries);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Add($"{formatter(state, exception)} {exception}");
        }
    }

    private class RepositoryProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> handler = null!;

        public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, RepositoryProxy>();
            ((RepositoryProxy)(object)proxy).handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var result = handler(targetMethod, args);
            if (result is not null && targetMethod.ReturnType.IsInstanceOfType(result))
                return result;
            if (targetMethod.ReturnType == typeof(Task))
                return Task.CompletedTask;
            if (targetMethod.ReturnType.IsGenericType
                && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [result]);
            }

            return result;
        }
    }

    private sealed record FindBySubjectCall(string Provider, string Issuer, string Subject);
    private sealed record GetOrCreateCall(User User, UserExternalIdentity Identity);
}
