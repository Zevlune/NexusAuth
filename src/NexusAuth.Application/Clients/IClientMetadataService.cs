namespace NexusAuth.Application.Clients;

public interface IClientMetadataService : IScopedDependency
{
    Task<ClientMetadataDto> GetAsync(CancellationToken ct = default);
}
