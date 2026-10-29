using System.Security.Cryptography;
using System.Text;
using Luck.DDD.Domain.Domain.Entities;

namespace NexusAuth.Domain.Entities;

public sealed class ConsentRequest : EntityWithIdentity<Guid>
{
    public const int RequestIdByteLength = 32;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// SHA-256 hash of the high-entropy request identifier, encoded as
    /// unpadded Base64Url. The raw identifier is returned to the caller once
    /// and is never persisted.
    /// </summary>
    public string RequestIdHash { get; private set; } = default!;

    public Guid UserId { get; private set; }

    public string Query { get; private set; } = default!;

    public ConsentRequestStatus Status { get; private set; }

    public bool Remember { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    private ConsentRequest(Guid id) : base(id)
    {
    }

    public static ConsentRequestCreationResult Create(
        Guid userId,
        string query,
        DateTimeOffset? createdAt = null)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var now = (createdAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var rawRequestId = GenerateRequestId();
        var request = new ConsentRequest(Guid.NewGuid())
        {
            RequestIdHash = HashRequestId(rawRequestId),
            UserId = userId,
            Query = query,
            Status = ConsentRequestStatus.Pending,
            Remember = false,
            CreatedAt = now,
            ExpiresAt = now.Add(Lifetime),
        };

        return new ConsentRequestCreationResult(request, rawRequestId);
    }

    public static string GenerateRequestId()
    {
        var bytes = RandomNumberGenerator.GetBytes(RequestIdByteLength);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string HashRequestId(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public void Approve(bool remember, DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        if (Status != ConsentRequestStatus.Pending || ExpiresAt <= now)
            throw new InvalidOperationException("Only an unexpired pending consent request can be approved.");

        Status = ConsentRequestStatus.Approved;
        Remember = remember;
        ApprovedAt = now;
    }

    public void Consume(DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        if (Status != ConsentRequestStatus.Approved || ExpiresAt <= now)
            throw new InvalidOperationException("Only an unexpired approved consent request can be consumed.");

        Status = ConsentRequestStatus.Consumed;
        ConsumedAt = now;
    }
}

public sealed record ConsentRequestCreationResult(ConsentRequest Entity, string RawRequestId);
