namespace NexusAuth.Application.Clients;

public record ClientMutationResultDto(
    ClientDto Client,
    GeneratedClientCredentialDto? GeneratedCredential);
