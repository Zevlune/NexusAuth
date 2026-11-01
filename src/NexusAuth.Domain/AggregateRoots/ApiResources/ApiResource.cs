using Luck.DDD.Domain.Domain.AggregateRoots;
using NexusAuth.Shared.OAuth;

namespace NexusAuth.Domain.AggregateRoots.ApiResources;

public class ApiResource : AggregateRootWithIdentity<Guid>
{
    public string Name { get; private set; } = default!;

    public string DisplayName { get; private set; } = default!;

    public string? Description { get; private set; }

    public string Audience { get; private set; } = default!;

    public bool IsActive { get; private set; }

    public bool IsSystemManaged { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// EF Core constructor
    /// </summary>
    private ApiResource(Guid id) : base(id)
    {
    }

    public static ApiResource Create(string name, string displayName, string audience, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ValidateAudience(audience);

        return new ApiResource(Guid.NewGuid())
        {
            Name = name,
            DisplayName = displayName,
            Audience = audience,
            Description = description,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void MarkAsSystemManaged()
    {
        IsSystemManaged = true;
    }

    public void Update(
        string? displayName = null,
        string? audience = null,
        string? description = null,
        bool? isActive = null)
    {
        if (!string.IsNullOrWhiteSpace(audience))
            ValidateAudience(audience);

        if (!string.IsNullOrWhiteSpace(displayName))
            DisplayName = displayName;

        if (!string.IsNullOrWhiteSpace(audience))
            Audience = audience;

        if (displayName is not null || audience is not null || description is not null)
            Description = description;

        if (isActive is { } nextIsActive)
            IsActive = nextIsActive;
    }

    private static void ValidateAudience(string audience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        if (OidcScopes.IsStandard(audience))
            throw new ArgumentException($"Audience '{audience}' is reserved for a standard OIDC scope.", nameof(audience));
    }
}
