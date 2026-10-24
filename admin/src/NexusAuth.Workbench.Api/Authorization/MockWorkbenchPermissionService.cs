using System.Security.Claims;
using Luck.Logging.Serilog;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Workbench.Api.Authorization;

/// <summary>
/// 用户存在且已启用时，从 mock 配置中读取并匹配 Workbench 权限。
/// </summary>
public sealed class MockWorkbenchPermissionService(
    IUserRepository users,
    IOptions<MockWorkbenchPermissionOptions> options,
    ILogger<MockWorkbenchPermissionService> logger)
    : IWorkbenchPermissionService
{
    private const string All = "*";
    private const string LogMessage =
        "Workbench authorization evaluated. UserId={UserId} HttpMethod={HttpMethod} Path={Path} " +
        "RoutePattern={RoutePattern} PermissionSource={PermissionSource} Stage={Stage} " +
        "ReasonCode={ReasonCode} Outcome={Outcome} RequestTraceId={RequestTraceId}";

    private static readonly WorkbenchPermission[] DefaultSystemAccountPermissions = [new(All, All)];

    public async Task<bool> HasPermissionAsync(
        ClaimsPrincipal user,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        var routePattern = GetRoutePattern(httpContext);
        Guid? userId = null;
        var permissionSource = "None";
        var stage = "RequestContext";

        using var scope = logger.BeginLuckLogScope(
            category: "WorkbenchAuthorization",
            subcategory: nameof(HasPermissionAsync),
            requestTraceId: httpContext?.TraceIdentifier);

        try
        {
            ct.ThrowIfCancellationRequested();

            if (httpContext is null)
                return Deny("RequestContext", "MissingHttpContext");

            stage = "Authentication";
            if (user.Identity?.IsAuthenticated != true)
                return Deny("Authentication", "Unauthenticated");

            stage = "Subject";
            var subject = user.FindFirst("sub")?.Value
                ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(subject, out var parsedUserId))
                return Deny("Subject", "InvalidSubject");
            userId = parsedUserId;

            stage = "UserLookup";
            ct.ThrowIfCancellationRequested();
            var account = await users.FindByIdAsync(parsedUserId, ct);
            ct.ThrowIfCancellationRequested();

            if (account is null)
                return Deny("UserLookup", "UserNotFound");
            if (!account.IsActive)
                return Deny("UserLookup", "UserInactive");

            stage = "PermissionLookup";
            var permissions = ResolvePermissions(
                parsedUserId, account, out permissionSource);
            if (permissions.Count == 0)
                return Deny("PermissionLookup", "NoPermissions");

            stage = "PermissionMatch";
            var pathMatched = false;
            foreach (var permission in permissions)
            {
                if (!MatchesPath(permission.Path, httpContext.Request.Path, routePattern))
                    continue;

                pathMatched = true;
                if (MatchesMethod(permission.HttpMethod, httpContext.Request.Method))
                {
                    LogDecision(
                        "Granted", "PermissionMatch", "PermissionMatched",
                        userId, httpContext, routePattern, permissionSource);
                    return true;
                }
            }

            if (!pathMatched)
                return Deny("PermissionMatch", "PathNotAllowed");
            return Deny("PermissionMatch", "MethodNotAllowed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogDecision(
                "Canceled", stage, "RequestCanceled",
                userId, httpContext, routePattern, permissionSource);
            throw;
        }
        catch (Exception exception)
        {
            LogDecision(
                "Error", stage, "PermissionEvaluationFailed",
                userId, httpContext, routePattern, permissionSource, exception);
            throw;
        }

        bool Deny(string deniedStage, string reasonCode)
        {
            LogDecision(
                "Denied", deniedStage, reasonCode,
                userId, httpContext, routePattern, permissionSource);
            return false;
        }
    }

    private static string? GetRoutePattern(HttpContext? httpContext) =>
        (httpContext?.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

    private static bool MatchesMethod(string configuredMethod, string requestMethod)
    {
        if (string.IsNullOrWhiteSpace(configuredMethod)
            || string.IsNullOrWhiteSpace(requestMethod))
            return false;

        return string.Equals(configuredMethod, All, StringComparison.Ordinal)
            || string.Equals(configuredMethod, requestMethod, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPath(
        string configuredPath,
        PathString requestPath,
        string? routePattern)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return false;
        if (string.IsNullOrWhiteSpace(requestPath.Value)
            && string.IsNullOrWhiteSpace(routePattern))
            return false;
        if (string.Equals(configuredPath, All, StringComparison.Ordinal))
            return true;

        return MatchesPathValue(configuredPath, requestPath.Value)
            || MatchesPathValue(configuredPath, routePattern);
    }

    private static bool MatchesPathValue(string configuredPath, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        return string.Equals(
            NormalizePath(configuredPath),
            NormalizePath(candidate),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.TrimEnd('/');
        return normalized.StartsWith('/') ? normalized : $"/{normalized}";
    }

    private IReadOnlyList<WorkbenchPermission> ResolvePermissions(
        Guid userId,
        User account,
        out string permissionSource)
    {
        var settings = options.Value;
        if (settings.UserPermissions.TryGetValue(userId.ToString(), out var userPermissions))
        {
            permissionSource = "UserPermissions";
            return userPermissions.ToArray();
        }

        if (account.IsSystemAccount)
        {
            if (settings.SystemAccountPermissions is not null)
            {
                permissionSource = "SystemAccountPermissions";
                return settings.SystemAccountPermissions.ToArray();
            }

            permissionSource = "DefaultSystemAccountPermissions";
            return DefaultSystemAccountPermissions.ToArray();
        }

        permissionSource = "None";
        return [];
    }

    private void LogDecision(
        string outcome,
        string stage,
        string reasonCode,
        Guid? userId,
        HttpContext? httpContext,
        string? routePattern,
        string permissionSource,
        Exception? exception = null)
    {
        object[] values =
        [
            userId?.ToString() ?? string.Empty,
            httpContext?.Request.Method ?? string.Empty,
            httpContext?.Request.Path.Value ?? string.Empty,
            routePattern ?? string.Empty,
            permissionSource,
            stage,
            reasonCode,
            outcome,
            httpContext?.TraceIdentifier ?? string.Empty,
        ];

        if (exception is not null)
        {
            logger.LogLuckError(
                exception,
                LogMessage,
                values,
                methodName: nameof(HasPermissionAsync));
        }
        else if (outcome == "Denied")
        {
            logger.LogLuckWarning(
                LogMessage,
                values,
                methodName: nameof(HasPermissionAsync));
        }
        else
        {
            logger.LogLuckInformation(
                LogMessage,
                values,
                methodName: nameof(HasPermissionAsync));
        }
    }
}
