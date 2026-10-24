using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusAuth.Workbench.Api.Authorization;
using System.Net;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchAuthorizationLoggingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_authorization_logs_success_and_each_denial_without_query_secrets(bool useCookie)
    {
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        using var fixture = new WorkbenchAdministrationAuthorizationFixture(
            "nexusauth.workbench", false, services =>
            {
                services.AddSingleton<ILogger<MockWorkbenchPermissionService>>(logger);
                services.Configure<MockWorkbenchPermissionOptions>(options =>
                    options.UserPermissions[WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId.ToString()] =
                    [new("GET", "/api/users")]);
            });
        using var anonymous = fixture.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/users?access_token=private-query-marker")).StatusCode);
        using var client = useCookie
            ? fixture.CreateAnonymousClient()
            : fixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);
        if (useCookie)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(
                $"/__test/sign-in/{WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/users?access_token=private-query-marker")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/users", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/clients/all")).StatusCode);

        AssertLog(logger, "PermissionMatched", "PermissionMatch", "Granted", LogLevel.Information);
        AssertLog(logger, "MethodNotAllowed", "PermissionMatch", "Denied", LogLevel.Warning);
        AssertLog(logger, "PathNotAllowed", "PermissionMatch", "Denied", LogLevel.Warning);
        AssertLog(logger, "Unauthenticated", "Authentication", "Denied", LogLevel.Warning);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain("private-query-marker", entry.Message);
            Assert.DoesNotContain(entry.Properties.Values,
                value => value?.ToString()?.Contains("private-query-marker", StringComparison.Ordinal) == true);
        });
    }

    [Theory]
    [InlineData(true, "UserInactive")]
    [InlineData(false, "UserNotFound")]
    public async Task Cookie_authorization_logs_the_database_account_denial(bool inactive, string reason)
    {
        var logger = new RecordingAuthorizationLogger<MockWorkbenchPermissionService>();
        using var fixture = new WorkbenchAdministrationAuthorizationFixture(
            "nexusauth.workbench", false, services =>
                services.AddSingleton<ILogger<MockWorkbenchPermissionService>>(logger));
        using var client = fixture.CreateAnonymousClient();
        var userId = inactive ? WorkbenchAdministrationAuthorizationFixture.InactiveUserId : Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/__test/sign-in/{userId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);

        AssertLog(logger, reason, "UserLookup", "Denied", LogLevel.Warning);
    }

    private static void AssertLog(
        RecordingAuthorizationLogger<MockWorkbenchPermissionService> logger,
        string reason, string stage, string outcome, LogLevel level)
    {
        var entry = Assert.Single(logger.Entries, entry =>
            entry.Properties.TryGetValue("ReasonCode", out var value) && Equals(value, reason));
        Assert.Equal(level, entry.Level);
        Assert.Equal(stage, entry.Properties["Stage"]);
        Assert.Equal(outcome, entry.Properties["Outcome"]);
        Assert.False(string.IsNullOrWhiteSpace(entry.Properties["RequestTraceId"]?.ToString()));
        Assert.Contains(entry.Properties.Values, value => Equals(value, "WorkbenchAuthorization"));
        Assert.Contains(entry.Properties.Values, value => Equals(value, "HasPermissionAsync"));
    }
}
