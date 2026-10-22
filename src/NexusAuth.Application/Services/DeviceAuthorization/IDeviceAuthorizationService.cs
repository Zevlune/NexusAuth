using Luck.Framework.Infrastructure.DependencyInjectionModule;
using NexusAuth.Application.Clients;

namespace NexusAuth.Application.Services.DeviceAuthorization;

public interface IDeviceAuthorizationService : IScopedDependency
{
    Task<DeviceAuthorizationStartResult> StartAsync(ClientAuthenticationInput authentication, string scope, CancellationToken ct = default);

    Task<DeviceAuthorizationPollResult> PollAsync(ClientAuthenticationInput authentication, string deviceCode, CancellationToken ct = default);

    Task<DeviceAuthorizationSessionResult> GetByUserCodeAsync(string userCode, CancellationToken ct = default);

    Task<DeviceAuthorizationSessionResult> ApproveAsync(string userCode, Guid userId, CancellationToken ct = default);

    Task<DeviceAuthorizationSessionResult> DenyAsync(string userCode, CancellationToken ct = default);
}
