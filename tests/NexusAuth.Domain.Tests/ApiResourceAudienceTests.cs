using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class ApiResourceAudienceTests
{
    public static IEnumerable<object[]> StandardScopes => OidcScopes.All.Select(scope => new object[] { scope });

    [Theory]
    [MemberData(nameof(StandardScopes))]
    public void Resource_audiences_cannot_use_standard_identity_scopes(string scope)
    {
        Assert.Throws<ArgumentException>(() => ApiResource.Create("resource", "Resource", scope));

        var resource = ApiResource.Create("resource", "Resource", "orders.api");
        Assert.Throws<ArgumentException>(() => resource.Update(displayName: "Changed", audience: scope));
        Assert.Equal("orders.api", resource.Audience);
        Assert.Equal("Resource", resource.DisplayName);
    }
}
