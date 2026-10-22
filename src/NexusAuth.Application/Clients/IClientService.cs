using NexusAuth.Application;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Application.Clients;

public interface IClientService : IScopedDependency
{
    #region OAuth 授权服务 (Host API 使用)

    Task<OAuthClient> RegisterClientAsync(
        string clientId,
        string clientName,
        string? description = null,
        IEnumerable<string>? redirectUris = null,
        IEnumerable<string>? postLogoutRedirectUris = null,
        IEnumerable<string>? allowedScopes = null,
        IEnumerable<string>? allowedGrantTypes = null,
        bool requirePkce = true,
        string tokenEndpointAuthMethod = OAuthClient.TokenEndpointAuthMethodClientSecretBasic,
        IEnumerable<OAuthClientSecret>? clientSecrets = null,
        CancellationToken ct = default);

    Task<OAuthClient?> ValidateClientAsync(
        string clientId,
        string rawClientSecret,
        CancellationToken ct = default);

    Task<ClientValidationResult> ValidateClientForAuthorizationAsync(
        string clientId,
        string redirectUri,
        string grantType,
        string? codeChallenge = null,
        string? codeChallengeMethod = null,
        CancellationToken ct = default);

    Task<ClientValidationResult> ValidateClientRedirectUriAsync(
        string clientId,
        string redirectUri,
        CancellationToken ct = default);

    Task<ClientAuthenticationResult> AuthenticateClientAsync(
        string clientId,
        string? rawClientSecret,
        bool requireSecret,
        CancellationToken ct = default);

    Task<ClientAuthenticationResult> AuthenticateClientAsync(
        ClientAuthenticationInput input,
        bool requireClientAuthentication,
        CancellationToken ct = default);

    Task<ClientAuthenticationResult> AuthenticateClientForPostLogoutAsync(
        string clientId,
        string? postLogoutRedirectUri,
        CancellationToken ct = default);

    Task<ScopeValidationResult> ValidateScopesAsync(
        string clientId,
        string scope,
        bool allowIdentityScopes,
        CancellationToken ct = default);

    #endregion

    #region 管理服务 (Workbench 使用)

    Task<List<ClientDto>> GetAllAsync(string? keyword = null, bool? isActive = null, CancellationToken ct = default);

    Task<PagedResult<ClientDto>> GetPagedAsync(
        string? keyword = null,
        bool? isActive = null,
        int page = 1,
        int pageSize = 10,
        CancellationToken ct = default);

    Task<ClientDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<ClientMutationResultDto> CreateAsync(CreateClientRequest request, CancellationToken ct = default);

    Task<ClientDto> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken ct = default);

    Task<ClientMutationResultDto> GenerateCredentialAsync(Guid id, GenerateClientCredentialRequest request, CancellationToken ct = default);

    Task<ClientMutationResultDto> ResetCredentialAsync(Guid id, GenerateClientCredentialRequest request, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    #endregion
}
