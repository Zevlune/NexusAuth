using Microsoft.AspNetCore.Mvc;
using NexusAuth.Application.Clients;
using NexusAuth.Workbench.Api.Filters;

namespace NexusAuth.Workbench.Api.Controllers;

[ApiController]
[WorkbenchApiResult]
[Route("api/client-metadata")]
public class ClientMetadataController(IClientMetadataService clientMetadataService) : ControllerBase
{
    [HttpGet]
    public async Task<ClientMetadataDto> Get(CancellationToken ct = default)
    {
        return await clientMetadataService.GetAsync(ct);
    }
}
