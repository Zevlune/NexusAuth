using Luck.Framework.Infrastructure.DependencyInjectionModule;

namespace NexusAuth.Application.Users;

public interface IUserManagementService : IScopedDependency
{
    Task<PagedResult<ManagedUserDto>> GetPagedAsync(string? keyword, bool? isActive, int page, int pageSize, CancellationToken ct = default);
    Task<ManagedUserDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ManagedUserDto> CreateAsync(CreateManagedUserRequest request, CancellationToken ct = default);
    Task<ManagedUserDto> UpdateProfileAsync(Guid id, UpdateManagedUserRequest request, CancellationToken ct = default);
    Task<ManagedUserDto> UpdateStatusAsync(Guid id, bool isActive, CancellationToken ct = default);
    Task ResetPasswordAsync(Guid id, ResetManagedUserPasswordRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task RevokeTokensAsync(Guid id, CancellationToken ct = default);
}
