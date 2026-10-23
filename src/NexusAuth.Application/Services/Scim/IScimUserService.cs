using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Application.Services.Scim;

public interface IScimUserService : IScopedDependency
{
    Task<ScimUser?> FindAsync(Guid id, CancellationToken ct = default);
    Task<ScimUserList> ListAsync(string? filter, int startIndex, int count, CancellationToken ct = default);
    Task<ScimUser> CreateAsync(ScimUserInput input, CancellationToken ct = default);
    Task<ScimUser?> ReplaceAsync(Guid id, ScimUserInput input, string? expectedVersion, CancellationToken ct = default);
    Task<ScimUser?> PatchAsync(Guid id, IReadOnlyList<ScimPatchOperation> operations, string? expectedVersion, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, string? expectedVersion, CancellationToken ct = default);
}
