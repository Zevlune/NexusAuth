using Demo.Mobile.Backend;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<MobileStore>();
builder.Services.AddHttpClient<OidcClient>(http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddControllers();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var callback = app.Configuration["Mobile:CallbackUri"];
if (callback != "nexusauth-demo://auth/callback")
    throw new InvalidOperationException("Mobile:CallbackUri must match the demo App scheme: nexusauth-demo://auth/callback.");
foreach (var name in new[] { "Authority", "RedirectUri" })
{
    if (!Uri.TryCreate(app.Configuration[$"NexusAuth:{name}"], UriKind.Absolute, out var uri)
        || (uri.Scheme != "https" && !(app.Environment.IsDevelopment() && uri.Scheme == "http")))
        throw new InvalidOperationException($"NexusAuth:{name} requires HTTPS outside Development.");
}
if (string.IsNullOrWhiteSpace(app.Configuration["NexusAuth:ClientSecret"]))
    throw new InvalidOperationException("Set NexusAuth__ClientSecret on the backend before starting.");
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.Run();

public partial class Program;
