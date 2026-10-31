using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>登录接口的返回方式。</summary>
public enum NexusAuthLoginResponse
{
    /// <summary>返回 <c>{ authorizeUrl }</c>，由 SPA 自行跳转。</summary>
    Json,

    /// <summary>直接 302 跳转到 NexusAuth.Host，适合服务端渲染应用。</summary>
    Redirect,
}

/// <summary><see cref="NexusAuthEndpoints.MapNexusAuthEndpoints"/> 的映射选项。</summary>
public sealed class NexusAuthEndpointOptions
{
    /// <summary>登录接口的返回方式。</summary>
    public NexusAuthLoginResponse LoginResponse { get; set; } = NexusAuthLoginResponse.Json;

    /// <summary>应用于登录接口的限流策略名称；为空时不限流。</summary>
    public string? LoginRateLimitPolicy { get; set; }

    /// <summary>是否映射 GET me；应用需要返回自定义用户信息时关闭并自行实现。</summary>
    public bool MapCurrentUser { get; set; } = true;
}

/// <summary>BFF 登录、登出与会话查询接口。授权码回调由标准 OIDC 中间件在 CallbackPath 处理。</summary>
public static class NexusAuthEndpoints
{
    /// <summary>默认路由前缀；Cookie 方案的 LoginPath 指向其下的 login。</summary>
    public const string DefaultPrefix = "/api/auth";

    private const string LoggerCategory = "NexusAuth.Extension.NexusAuthEndpoints";

    /// <summary>
    /// 映射 GET config、GET login、POST logout 和可选的 GET me。
    /// 返回路由组，便于应用追加 CORS、标签等约定。
    /// </summary>
    public static RouteGroupBuilder MapNexusAuthEndpoints(
        this IEndpointRouteBuilder endpoints,
        string prefix = DefaultPrefix,
        Action<NexusAuthEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new NexusAuthEndpointOptions();
        configure?.Invoke(options);

        var group = endpoints.MapGroup(prefix);
        // 这些是 API 端点：未登录时 Cookie 方案返回 401/403，而不是 302 到登录页。
        group.DisableCookieRedirect();
        // 认证接口的响应包含会话状态或一次性地址，任何缓存都不应保存。
        group.AddEndpointFilter(static async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapGet("/config", static (IOptions<NexusAuthClientOptions> client) => Results.Ok(new
        {
            authority = client.Value.Authority,
            clientId = client.Value.ClientId,
            redirectUri = client.Value.RedirectUri,
        })).AllowAnonymous();

        var login = group.MapGet("/login", (HttpContext context, string? returnUrl, CancellationToken ct) =>
            LoginAsync(context, returnUrl, options.LoginResponse, ct)).AllowAnonymous();
        if (!string.IsNullOrWhiteSpace(options.LoginRateLimitPolicy))
            login.RequireRateLimiting(options.LoginRateLimitPolicy);

        // 会话操作只要求已登录，不受应用默认授权策略（例如管理员权限）限制。
        group.MapPost("/logout", LogoutAsync)
            .RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        if (options.MapCurrentUser)
            group.MapGet("/me", static (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
                ? Results.Ok(new
                {
                    isAuthenticated = true,
                    user = new
                    {
                        id = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"),
                        name = user.Identity.Name,
                    },
                })
                : Results.Json(new { isAuthenticated = false }, statusCode: StatusCodes.Status401Unauthorized))
                .AllowAnonymous();

        return group;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context, string? returnUrl, NexusAuthLoginResponse mode, CancellationToken ct)
    {
        var services = context.RequestServices;
        var settings = services.GetRequiredService<IOptions<NexusAuthClientOptions>>().Value;
        var setup = services.GetRequiredService<NexusAuthClientSetup>();
        var flows = services.GetRequiredService<OpenIdConnectFlowState>();
        var target = NexusAuthPaths.IsLocalPath(returnUrl) ? returnUrl : settings.Frontend.CallbackPath;
        var properties = new AuthenticationProperties
        {
            RedirectUri = settings.FrontendBaseUri + target,
            IsPersistent = true,
            AllowRefresh = true,
        };
        try
        {
            await flows.BeginAsync(context, properties, setup.OpenIdConnectScheme, ct);
            // challenge 由标准中间件构造授权请求，生成 state、nonce 和 PKCE code_challenge。
            await context.ChallengeAsync(setup.OpenIdConnectScheme, properties);
            if (mode == NexusAuthLoginResponse.Redirect)
                return Results.Empty;

            var authorizeUrl = context.Response.Headers.Location.ToString();
            if (string.IsNullOrEmpty(authorizeUrl))
                throw new InvalidOperationException("The provider did not supply an authorization URL.");
            context.Response.Headers.Remove("Location");
            return Results.Ok(new { authorizeUrl });
        }
        catch (FlowStateCapacityExceededException)
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.Json(new { error = "too_many_login_requests" }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await flows.CancelAsync(context, properties, setup.OpenIdConnectScheme, CancellationToken.None);
            context.Response.Headers.Remove("Set-Cookie");
            context.Response.Headers.Remove("Location");
            Logger(context).LogWarning("Provider discovery failed during login. FailureType={FailureType}",
                exception.GetType().Name);
            return Results.Json(new { error = "provider_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, CancellationToken ct)
    {
        var services = context.RequestServices;
        var settings = services.GetRequiredService<IOptions<NexusAuthClientOptions>>().Value;
        var setup = services.GetRequiredService<NexusAuthClientSetup>();
        var client = services.GetRequiredService<INexusAuthTokenClient>();
        var logger = Logger(context);

        // Bearer 调用撤销本次 Access Token；浏览器调用同时清理 Cookie 和 Provider 会话。
        var bearerRequest = setup.EnableBearer && context.Request.Headers.ContainsKey("Authorization");
        var scheme = bearerRequest ? setup.BearerScheme : setup.CookieScheme;
        var logoutUrl = string.Empty;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var tokenName in new[] { "refresh_token", "access_token" })
            {
                var token = await context.GetTokenAsync(scheme, tokenName);
                if (string.IsNullOrEmpty(token)) continue;
                try { await client.RevokeTokenAsync(token, tokenName, timeout.Token); }
                catch (Exception exception)
                {
                    logger.LogWarning("Provider token revocation failed during logout. TokenType={TokenType} FailureType={FailureType}",
                        tokenName, exception.GetType().Name);
                }
            }
            if (!bearerRequest && settings.SignOutProvider
                && !string.IsNullOrEmpty(await context.GetTokenAsync(setup.CookieScheme, "id_token")))
            {
                try
                {
                    // 在共享的登出时限内预热处理器自身的元数据缓存。
                    await services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                        .Get(setup.OpenIdConnectScheme).ConfigurationManager!
                        .GetConfigurationAsync(timeout.Token).WaitAsync(timeout.Token);
                    await context.SignOutAsync(setup.OpenIdConnectScheme,
                        new AuthenticationProperties { RedirectUri = settings.PostLogoutRedirectUri });
                    logoutUrl = context.Response.Headers.Location.ToString();
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Provider sign-out failed. FailureType={FailureType}", exception.GetType().Name);
                }
            }
        }
        finally
        {
            if (!bearerRequest)
                await context.SignOutAsync(setup.CookieScheme);
        }
        context.Response.Headers.Remove("Location");
        return Results.Ok(new { logoutUrl });
    }

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
}
