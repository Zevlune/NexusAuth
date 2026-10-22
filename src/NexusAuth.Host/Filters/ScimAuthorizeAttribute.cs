using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NexusAuth.Application.Services.Scim;

namespace NexusAuth.Host.Filters;

/// <summary>
/// Authenticates a SCIM service-principal bearer token. A missing, unknown, expired,
/// disabled or revoked token yields 401 with <c>WWW-Authenticate: Bearer</c>; a valid
/// token without the required scope yields 403 (RFC 6750 §3.1).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ScimAuthorizeAttribute(string requiredScope)
    : Attribute, IFilterFactory, IOrderedFilter
{
    public bool IsReusable => false;

    public int Order => int.MinValue + 100;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var credentialService = serviceProvider.GetRequiredService<IScimCredentialService>();
        return new ScimAuthorizationFilter(credentialService, requiredScope);
    }

    private sealed class ScimAuthorizationFilter(IScimCredentialService credentialService,
        string requiredScope) : IAsyncAuthorizationFilter
    {
        private const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";
        private const string ScimContentType = "application/scim+json";

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var authorization = context.HttpContext.Request.Headers.Authorization.ToString();
            var hasBearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            var result = hasBearer
                ? await credentialService.AuthorizeAsync(
                    authorization[7..].Trim(),
                    requiredScope,
                    context.HttpContext.RequestAborted)
                : ScimCredentialValidationResult.InvalidToken;

            if (result == ScimCredentialValidationResult.Granted)
                return;

            var forbidden = result == ScimCredentialValidationResult.InsufficientScope;
            var statusCode = forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
            context.HttpContext.Response.Headers.WWWAuthenticate = forbidden
                ? $"Bearer error=\"insufficient_scope\", scope=\"{requiredScope}\""
                : hasBearer ? "Bearer error=\"invalid_token\"" : "Bearer";
            context.Result = new JsonResult(new
            {
                schemas = new[] { ErrorSchema },
                status = statusCode.ToString(),
                scimType = forbidden ? "forbidden" : "invalidToken",
                detail = forbidden
                    ? $"The credential does not grant the '{requiredScope}' scope."
                    : "A valid SCIM bearer token is required."
            })
            {
                StatusCode = statusCode,
                ContentType = ScimContentType
            };
        }
    }
}
