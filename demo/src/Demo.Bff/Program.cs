using System.Collections.Concurrent;
using Demo.Bff.Models;
using Demo.Bff.Options;
using Demo.Bff.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

var nexusAuthSection = builder.Configuration.GetSection("NexusAuth");
var authority = nexusAuthSection["Authority"]!;

var jwtSection = builder.Configuration.GetSection("Jwt");
var issuer = jwtSection["Issuer"]!;
// 只接受签发给本 API 资源的令牌。不要加入 Host 的 DefaultAudience（"NexusAuth"）：
// 任何未请求资源 scope 的令牌都会使用该 aud，接受它会让发给其他客户端的令牌也能调用本 API。
var audience = jwtSection["Audience"] ?? "demo-bff-api";
var sessionSection = builder.Configuration.GetSection("Session");
var sessionOptions = sessionSection.Get<BffSessionOptions>() ?? new BffSessionOptions();
var machineApiSection = builder.Configuration.GetSection("MachineApi");
var machineApiOptions = machineApiSection.Get<MachineApiOptions>() ?? new MachineApiOptions();

builder.Services.Configure<FrontendOptions>(builder.Configuration.GetSection("Frontend"));
builder.Services.Configure<NexusAuthBffOptions>(builder.Configuration.GetSection("NexusAuth"));
builder.Services.Configure<BffSessionOptions>(sessionSection);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = sessionOptions.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionOptions.CookieLifetimeMinutes);
        options.EventsType = typeof(OidcSessionCookieEvents);
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.Authority = authority;
        options.RequireHttpsMetadata = false;
        // 保留 sub / scope / client_id 等原始 claim 名称，便于下方授权策略按 OAuth 语义校验。
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidIssuer = issuer;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidAudience = audience;
        options.TokenValidationParameters.ValidateLifetime = true;
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();

    // aud 校验之外，再限定 scope 与调用方 client_id，并拒绝 id_token 等非 access_token。
    options.AddPolicy(MachineApiOptions.PolicyName, policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
        {
            var user = context.User;
            var tokenUse = user.FindFirst("token_use")?.Value;
            if (tokenUse is not null && !string.Equals(tokenUse, "access_token", StringComparison.Ordinal))
                return false;

            var clientId = user.FindFirst("client_id")?.Value;
            if (clientId is null || !machineApiOptions.AllowedClientIds.Contains(clientId, StringComparer.Ordinal))
                return false;

            return user.FindAll("scope")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(machineApiOptions.RequiredScope, StringComparer.Ordinal);
        }));
});
builder.Services.AddHttpClient();
builder.Services.AddControllers();
builder.Services.AddSingleton<ConcurrentDictionary<string, OidcFlowState>>();
builder.Services.AddScoped<OidcBffService>();
builder.Services.AddScoped<OidcSessionCookieEvents>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
