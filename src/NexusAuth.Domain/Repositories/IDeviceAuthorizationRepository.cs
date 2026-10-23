using Luck.DDD.Domain.Repositories;
using Luck.Framework.Infrastructure.DependencyInjectionModule;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Domain.Repositories;

public interface IDeviceAuthorizationRepository : IEntityRepository<DeviceAuthorization, Guid>, IScopedDependency
{
    Task<DeviceAuthorization?> FindByDeviceCodeAsync(string deviceCode, CancellationToken ct = default);

    Task<DeviceAuthorization?> FindByDeviceCodeHashAsync(string deviceCodeHash, CancellationToken ct = default);

    Task<DeviceAuthorization?> FindByUserCodeAsync(string normalizedUserCode, CancellationToken ct = default);

    Task AddAsync(DeviceAuthorization authorization, CancellationToken ct = default);

    Task UpdateAsync(DeviceAuthorization authorization, CancellationToken ct = default);

    Task<bool> ApprovePendingAsync(
        Guid id,
        Guid userId,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<bool> DenyPendingAsync(
        Guid id,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically records a poll against the expected previous timestamp.
    /// The compare-and-update prevents concurrent pollers from losing the
    /// interval/backoff update.
    /// </summary>
    Task<bool> TryRegisterPollAsync(
        Guid id,
        DateTimeOffset? expectedLastPolledAt,
        DateTimeOffset now,
        int pollingIntervalSeconds,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically consumes an approved authorization for the owning client.
    /// </summary>
    Task<DeviceAuthorization?> ConsumeApprovedAsync(
        string deviceCodeHash,
        string clientId,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// 中文注释：把某个客户端仍处于 Pending / Approved（尚未兑换）的设备授权全部置为 Denied。
    /// 主要调用方：删除客户端时清理遗留授权。
    /// </summary>
    Task<int> DenyOutstandingForClientAsync(string clientId, CancellationToken ct = default);
}
