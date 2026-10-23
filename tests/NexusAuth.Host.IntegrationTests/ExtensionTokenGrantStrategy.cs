using Microsoft.AspNetCore.Mvc;
using NexusAuth.Host.TokenEndpoint;
using NexusAuth.Shared.DependencyInjection;

namespace NexusAuth.Host.IntegrationTests;

[KeyedService(typeof(ITokenGrantStrategy), "extension")]
public class ExtensionTokenGrantStrategy : ITokenGrantStrategy
{
    public Task<IActionResult> ExecuteAsync(TokenGrantContext context, CancellationToken ct) =>
        Task.FromResult<IActionResult>(new OkObjectResult(ct));
}
