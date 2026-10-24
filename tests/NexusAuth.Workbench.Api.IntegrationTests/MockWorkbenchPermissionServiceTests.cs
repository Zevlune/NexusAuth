using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Workbench.Api.Authorization;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class MockWorkbenchPermissionServiceTests
{
    private static readonly Guid ConfiguredUserId =
        Guid.Parse("7f433a2c-3793-4d69-810e-dae854cd7734");

    [Fact]
    public async Task Enabled_system_accounts_use_the_wildcard_default()
    {
        var user = CreateUser("system", systemAccount: true);
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var service = CreateService(
            CreateRepository(user), new MockWorkbenchPermissionOptions(), logger);

        var allowed = await service.HasPermissionAsync(
            CreatePrincipal(user.Id), CreateRequest("PATCH", "/api/unconfigured"));

        Assert.True(allowed);
        var entry = AssertLog(
            logger, "PermissionMatched", "PermissionMatch", "Granted", LogLevel.Information);
        Assert.Equal("DefaultSystemAccountPermissions", entry.Properties["PermissionSource"]);
    }

    [Theory]
    [InlineData("GET", "/api/users", "GET", "/api/users", null, true)]
    [InlineData("get", "api/users", "GET", "/API/USERS/", null, true)]
    [InlineData(
        "GET",
        "/api/users/{id:guid}",
        "GET",
        "/api/users/00000000-0000-0000-0000-000000000001",
        "/API/USERS/{id:guid}/",
        true)]
    [InlineData("GET", "/api/users", "GET", "/not-the-request-path", "api/users", true)]
    [InlineData("*", "*", "PATCH", "/unmapped", null, true)]
    [InlineData("GET", "/api/*", "GET", "/api/users", "/api/users", false)]
    [InlineData("GET", "/api/users", "GET", "//api/users", null, false)]
    [InlineData("GET", "/api/users", "POST", "/api/users", null, false)]
    [InlineData("GET", "/api/users", "GET", "/api/clients", null, false)]
    public async Task Permission_matching_preserves_the_original_handler_matrix(
        string configuredMethod,
        string configuredPath,
        string requestMethod,
        string requestPath,
        string? routePattern,
        bool expected)
    {
        var user = CreateUser("ordinary");
        var options = new MockWorkbenchPermissionOptions
        {
            UserPermissions =
            {
                [user.Id.ToString()] = [new(configuredMethod, configuredPath)],
            },
        };
        var service = CreateService(CreateRepository(user), options);

        var allowed = await service.HasPermissionAsync(
            CreatePrincipal(user.Id), CreateRequest(requestMethod, requestPath, routePattern));

        Assert.Equal(expected, allowed);
    }

    [Fact]
    public async Task Paths_match_on_a_leading_slash_normalized_candidate()
    {
        var user = CreateUser("ordinary");
        var options = new MockWorkbenchPermissionOptions
        {
            UserPermissions =
            {
                [user.Id.ToString()] = [new("GET", "/api/users")],
            },
        };
        var service = CreateService(CreateRepository(user), options);
        var request = CreateRequest("GET", "/not-the-request-path", "api/users");

        Assert.True(await service.HasPermissionAsync(CreatePrincipal(user.Id), request));
    }

    [Fact]
    public async Task Path_and_method_must_match_the_same_permission()
    {
        var user = CreateUser("ordinary");
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var options = new MockWorkbenchPermissionOptions
        {
            UserPermissions =
            {
                [user.Id.ToString()] =
                [
                    new("GET", "/api/users"),
                    new("POST", "/api/clients"),
                ],
            },
        };
        var service = CreateService(CreateRepository(user), options, logger);

        var allowed = await service.HasPermissionAsync(
            CreatePrincipal(user.Id), CreateRequest("POST", "/api/users"));

        Assert.False(allowed);
        AssertLog(
            logger, "MethodNotAllowed", "PermissionMatch", "Denied", LogLevel.Warning);
    }

    [Theory]
    [InlineData(" ", "/api/users", "POST", "/api/users", "MethodNotAllowed")]
    [InlineData("GET", "  ", "GET", "/api/users", "PathNotAllowed")]
    public async Task Empty_or_whitespace_configured_permission_fields_are_rejected(
        string configuredMethod,
        string configuredPath,
        string requestMethod,
        string requestPath,
        string expectedReason)
    {
        var user = CreateUser("ordinary");
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var options = new MockWorkbenchPermissionOptions
        {
            UserPermissions =
            {
                [user.Id.ToString()] = [new(configuredMethod, configuredPath)],
            },
        };
        var service = CreateService(CreateRepository(user), options, logger);

        var allowed = await service.HasPermissionAsync(
            CreatePrincipal(user.Id), CreateRequest(requestMethod, requestPath));

        Assert.False(allowed);
        AssertLog(logger, expectedReason, "PermissionMatch", "Denied", LogLevel.Warning);
    }

    [Theory]
    [InlineData("missing-context", "MissingHttpContext", "RequestContext", 0)]
    [InlineData("unauthenticated", "Unauthenticated", "Authentication", 0)]
    [InlineData("invalid-subject", "InvalidSubject", "Subject", 0)]
    [InlineData("user-not-found", "UserNotFound", "UserLookup", 1)]
    [InlineData("user-inactive", "UserInactive", "UserLookup", 1)]
    [InlineData("no-permissions", "NoPermissions", "PermissionLookup", 1)]
    [InlineData("path-denied", "PathNotAllowed", "PermissionMatch", 1)]
    [InlineData("method-denied", "MethodNotAllowed", "PermissionMatch", 1)]
    public async Task Every_denial_stage_returns_and_logs_its_reason(
        string scenario,
        string expectedReason,
        string expectedStage,
        int expectedRepositoryCalls)
    {
        var activeUser = CreateUser("ordinary");
        var inactiveUser = CreateUser("inactive", active: false);
        var account = scenario switch
        {
            "user-not-found" => null,
            "user-inactive" => inactiveUser,
            _ => activeUser,
        };
        var subject = scenario == "invalid-subject"
            ? "not-a-guid"
            : (account?.Id ?? ConfiguredUserId).ToString();
        var principal = CreatePrincipal(
            authenticated: scenario != "unauthenticated",
            subject);
        var httpContext = scenario == "missing-context"
            ? null
            : CreateRequest(
                scenario == "method-denied" ? "POST" : "GET",
                "/api/users");
        var options = new MockWorkbenchPermissionOptions();
        if (scenario is "path-denied" or "method-denied")
        {
            options.UserPermissions[activeUser.Id.ToString()] =
                [new("GET", scenario == "path-denied" ? "/api/clients" : "/api/users")];
        }

        var repositoryCalls = 0;
        var users = RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
        {
            Assert.Equal(nameof(IUserRepository.FindByIdAsync), method.Name);
            repositoryCalls++;
            return account;
        });
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var service = CreateService(users, options, logger);

        var allowed = await service.HasPermissionAsync(principal, httpContext);

        Assert.False(allowed);
        Assert.Equal(expectedRepositoryCalls, repositoryCalls);
        AssertLog(
            logger, expectedReason, expectedStage, "Denied", LogLevel.Warning);
    }

    [Fact]
    public async Task Explicit_permissions_use_the_request_identity_and_token_and_hide_query_values()
    {
        var user = CreateUser("ordinary");
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var options = new MockWorkbenchPermissionOptions
        {
            UserPermissions =
            {
                [user.Id.ToString()] = [new("POST", "/api/users")],
            },
        };
        Guid repositoryUserId = default;
        CancellationToken repositoryToken = default;
        var repositoryCalls = 0;
        var users = RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
        {
            repositoryCalls++;
            repositoryUserId = (Guid)args![0]!;
            repositoryToken = (CancellationToken)args[1]!;
            return user;
        });
        var service = CreateService(users, options, logger);
        var request = CreateRequest("post", "/api/users");
        request.Request.QueryString = new QueryString("?access_token=private-query-marker");
        using var cancellationSource = new CancellationTokenSource();

        var allowed = await service.HasPermissionAsync(
            CreatePrincipal(user.Id), request, cancellationSource.Token);

        Assert.True(allowed);
        Assert.Equal(user.Id, repositoryUserId);
        Assert.Equal(cancellationSource.Token, repositoryToken);
        Assert.Equal(1, repositoryCalls);
        var entry = AssertLog(
            logger, "PermissionMatched", "PermissionMatch", "Granted", LogLevel.Information);
        Assert.Equal(user.Id.ToString(), entry.Properties["UserId"]);
        Assert.Equal("post", entry.Properties["HttpMethod"]);
        Assert.Equal("/api/users", entry.Properties["Path"]);
        Assert.Equal("trace-id", entry.Properties["RequestTraceId"]);
        Assert.Equal("UserPermissions", entry.Properties["PermissionSource"]);
        Assert.All(logger.Entries, recorded =>
        {
            Assert.DoesNotContain("private-query-marker", recorded.Message);
            Assert.DoesNotContain(
                recorded.Properties.Values,
                value => value?.ToString()?.Contains(
                    "private-query-marker", StringComparison.Ordinal) == true);
        });
    }

    [Fact]
    public async Task Empty_explicit_user_permissions_override_system_account_defaults()
    {
        var user = CreateUser("system", systemAccount: true);
        var options = new MockWorkbenchPermissionOptions
        {
            UserPermissions =
            {
                [user.Id.ToString()] = [],
            },
        };
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var service = CreateService(CreateRepository(user), options, logger);

        var allowed = await service.HasPermissionAsync(
            CreatePrincipal(user.Id), CreateRequest("GET", "/api/users"));

        Assert.False(allowed);
        AssertLog(logger, "NoPermissions", "PermissionLookup", "Denied", LogLevel.Warning);
    }

    [Fact]
    public async Task Repository_failure_logs_the_error_and_is_rethrown_unchanged()
    {
        var user = CreateUser("ordinary");
        var failure = new InvalidOperationException("Permission source is unavailable.");
        var users = RecordingDispatchProxy.Create<IUserRepository>((_, _) => throw failure);
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var service = CreateService(users, new MockWorkbenchPermissionOptions(), logger);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.HasPermissionAsync(
                CreatePrincipal(user.Id), CreateRequest("GET", "/api/users")));

        Assert.Same(failure, actual);
        var entry = AssertLog(
            logger,
            "PermissionEvaluationFailed",
            "UserLookup",
            "Error",
            LogLevel.Error);
        Assert.Same(failure, entry.Exception);
    }

    [Fact]
    public async Task A_canceled_request_does_not_query_the_user_repository()
    {
        var user = CreateUser("ordinary");
        var repositoryCalls = 0;
        var users = RecordingDispatchProxy.Create<IUserRepository>((_, _) =>
        {
            repositoryCalls++;
            return user;
        });
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var service = CreateService(users, new MockWorkbenchPermissionOptions(), logger);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.HasPermissionAsync(
                CreatePrincipal(user.Id),
                CreateRequest("GET", "/api/users"),
                cancellationSource.Token));

        Assert.Equal(0, repositoryCalls);
        AssertLog(logger, "RequestCanceled", "RequestContext", "Canceled", LogLevel.Information);
    }

    [Fact]
    public async Task Cancellation_after_user_lookup_is_logged_and_rethrown()
    {
        var user = CreateUser("ordinary");
        using var cancellationSource = new CancellationTokenSource();
        var users = RecordingDispatchProxy.Create<IUserRepository>((_, _) =>
        {
            cancellationSource.Cancel();
            return user;
        });
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var service = CreateService(users, new MockWorkbenchPermissionOptions(), logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.HasPermissionAsync(
                CreatePrincipal(user.Id),
                CreateRequest("GET", "/api/users"),
                cancellationSource.Token));

        AssertLog(logger, "RequestCanceled", "UserLookup", "Canceled", LogLevel.Information);
    }

    [Fact]
    public async Task Configuration_binding_uses_specific_system_account_permissions_without_the_wildcard()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MockWorkbenchPermissionOptions.SectionName}:SystemAccountPermissions:0:HttpMethod"] = "GET",
                [$"{MockWorkbenchPermissionOptions.SectionName}:SystemAccountPermissions:0:Path"] = "/api/users",
            })
            .Build();
        var user = CreateUser("system", systemAccount: true);
        using var provider = CreateAuthorizationProvider(configuration, CreateRepository(user));
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        var service = new MockWorkbenchPermissionService(
            CreateRepository(user),
            provider.GetRequiredService<IOptions<MockWorkbenchPermissionOptions>>(),
            logger);

        Assert.True(await service.HasPermissionAsync(
            CreatePrincipal(user.Id), CreateRequest("GET", "/api/users")));
        Assert.False(await service.HasPermissionAsync(
            CreatePrincipal(user.Id), CreateRequest("GET", "/api/clients")));
        AssertLog(
            logger, "PermissionMatched", "PermissionMatch", "Granted", LogLevel.Information);
        var denied = AssertLog(
            logger, "PathNotAllowed", "PermissionMatch", "Denied", LogLevel.Warning);
        Assert.Equal("SystemAccountPermissions", denied.Properties["PermissionSource"]);
    }

    [Fact]
    public async Task Empty_system_account_permissions_in_json_do_not_restore_the_default_wildcard()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"Authorization":{"MockPermissions":{"SystemAccountPermissions":[]}}}"""));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        var user = CreateUser("system", systemAccount: true);
        var users = CreateRepository(user);
        using var provider = CreateAuthorizationProvider(configuration, users);
        var service = new MockWorkbenchPermissionService(
            users,
            provider.GetRequiredService<IOptions<MockWorkbenchPermissionOptions>>(),
            new RecordingAuthorizationLogger<MockWorkbenchPermissionService>());

        Assert.False(await service.HasPermissionAsync(
            CreatePrincipal(user.Id), CreateRequest("GET", "/api/users")));
    }

    [Fact]
    public async Task Empty_user_permissions_in_json_override_system_account_defaults()
    {
        var user = CreateUser("system", systemAccount: true);
        var settings = new
        {
            Authorization = new
            {
                MockPermissions = new
                {
                    UserPermissions = new Dictionary<string, WorkbenchPermission[]>
                    {
                        [user.Id.ToString()] = [],
                    },
                },
            },
        };
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(settings));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        var users = CreateRepository(user);
        using var provider = CreateAuthorizationProvider(configuration, users);
        var service = new MockWorkbenchPermissionService(
            users,
            provider.GetRequiredService<IOptions<MockWorkbenchPermissionOptions>>(),
            new RecordingAuthorizationLogger<MockWorkbenchPermissionService>());

        Assert.False(await service.HasPermissionAsync(
            CreatePrincipal(user.Id), CreateRequest("GET", "/api/users")));
    }

    [Fact]
    public async Task Configuration_binding_applies_guid_keyed_user_permissions()
    {
        var user = CreateUser("ordinary");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MockWorkbenchPermissionOptions.SectionName}:UserPermissions:{user.Id}:0:HttpMethod"] = "DELETE",
                [$"{MockWorkbenchPermissionOptions.SectionName}:UserPermissions:{user.Id}:0:Path"] =
                    "/api/users/{id:guid}",
            })
            .Build();
        var users = CreateRepository(user);
        using var provider = CreateAuthorizationProvider(configuration, users);
        var service = new MockWorkbenchPermissionService(
            users,
            provider.GetRequiredService<IOptions<MockWorkbenchPermissionOptions>>(),
            new RecordingAuthorizationLogger<MockWorkbenchPermissionService>());
        var request = CreateRequest(
            "DELETE",
            "/api/users/00000000-0000-0000-0000-000000000001",
            "/api/users/{id:guid}");

        Assert.True(await service.HasPermissionAsync(CreatePrincipal(user.Id), request));
    }

    private static MockWorkbenchPermissionService CreateService(
        IUserRepository users,
        MockWorkbenchPermissionOptions options,
        RecordingAuthorizationLogger<MockWorkbenchPermissionService>? logger = null) =>
        new(
            users,
            Options.Create(options),
            logger ?? new RecordingAuthorizationLogger<MockWorkbenchPermissionService>());

    private static ServiceProvider CreateAuthorizationProvider(
        IConfiguration configuration,
        IUserRepository users)
    {
        var services = new ServiceCollection();
        services.AddWorkbenchAuthorization(configuration);
        services.AddSingleton(users);
        return services.BuildServiceProvider();
    }

    private static IUserRepository CreateRepository(User? user) =>
        RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
        {
            Assert.Equal(nameof(IUserRepository.FindByIdAsync), method.Name);
            return (Guid)args![0]! == user?.Id ? user : null;
        });

    private static DefaultHttpContext CreateRequest(
        string method,
        string path,
        string? routePattern = null)
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-id",
        };
        context.Request.Method = method;
        context.Request.Path = path;
        if (routePattern is not null)
        {
            context.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(routePattern),
                order: 0,
                metadata: null,
                displayName: null));
        }

        return context;
    }

    private static ClaimsPrincipal CreatePrincipal(bool authenticated, string? subject)
    {
        Claim[] claims = authenticated && subject is not null
            ? [new Claim("sub", subject)]
            : [];
        var identity = new ClaimsIdentity(claims, authenticated ? "test" : null);
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreatePrincipal(Guid userId) =>
        CreatePrincipal(authenticated: true, userId.ToString());

    private static User CreateUser(
        string username,
        bool active = true,
        bool systemAccount = false)
    {
        var user = User.Create(username, "test-password", username);
        if (!active)
            user.UpdateScimProfile(username, username, isActive: false);
        if (systemAccount)
            user.MarkAsSystemAccount();
        return user;
    }

    private static (
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties) AssertLog(
        RecordingAuthorizationLogger<MockWorkbenchPermissionService> logger,
        string reason,
        string stage,
        string outcome,
        LogLevel level)
    {
        var entry = Assert.Single(logger.Entries, candidate =>
            candidate.Properties.TryGetValue("ReasonCode", out var value)
            && Equals(value, reason));
        Assert.Equal(level, entry.Level);
        Assert.Equal(stage, entry.Properties["Stage"]);
        Assert.Equal(outcome, entry.Properties["Outcome"]);
        return entry;
    }
}
