using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Shared.DependencyInjection;
using static NexusAuth.Host.TokenEndpoint.TokenGrantHelper;

namespace NexusAuth.Host.TokenEndpoint;

[KeyedService(typeof(ITokenGrantStrategy), TokenGrantKeys.Unsupported)]
public class UnsupportedTokenGrantStrategy(IOptions<JwtOptions> jwtOptions) : ITokenGrantStrategy
{
    public Task<IActionResult> ExecuteAsync(TokenGrantContext context, CancellationToken ct)
    {
        if (ParseClientAuthentication(context, jwtOptions.Value.Issuer) is null)
            return Task.FromResult(InvalidClient(context.HttpContext));

        return Task.FromResult<IActionResult>(new BadRequestObjectResult(new
        {
            error = "unsupported_grant_type",
            error_description = $"Grant type '{context.Request.GrantType}' is not supported.",
        }));
    }
}
