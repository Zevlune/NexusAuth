using NexusAuth.Domain.Entities;
using Xunit;

namespace NexusAuth.Domain.Tests;

public sealed class CredentialRevocationTests
{
    [Fact]
    public void Scim_revocation_is_terminal_while_disable_is_reversible()
    {
        var credential = ScimServicePrincipalCredential.CreateWithToken("scim-client", "raw-token", ["scim:read"]);

        credential.SetActive(false);
        Assert.False(credential.CanAuthenticate());
        Assert.Null(credential.RevokedAt);
        credential.SetActive(true);
        Assert.True(credential.CanAuthenticate());

        credential.Revoke();
        Assert.Throws<InvalidOperationException>(() => credential.SetActive(true));
        Assert.False(credential.IsActive);
        Assert.NotNull(credential.RevokedAt);
        Assert.False(credential.CanAuthenticate());
    }

    [Fact]
    public void Scim_update_with_null_scopes_keeps_scopes_and_rejects_unknown_scopes()
    {
        var credential = ScimServicePrincipalCredential.CreateWithToken("scim-client", "raw-token", ["scim:read"]);

        credential.Update("scim-client", scopes: null, expiresAt: null);
        Assert.Equal(["scim:read"], credential.Scopes);

        Assert.Throws<ArgumentException>(() => credential.Update("scim-client", ["scim:read", "admin"], null));
        Assert.Equal(["scim:read"], credential.Scopes);
        Assert.Throws<ArgumentException>(() => ScimServicePrincipalCredential.CreateWithToken("other", "raw-token-2", ["Scim:Write"]));
    }

    [Fact]
    public void Open_api_revocation_is_terminal_and_keeps_the_first_revocation_time()
    {
        var credential = OpenApiCredential.CreateWithToken("reader", "raw-token", OpenApiCredential.TargetTypeApplication);

        credential.Update("reader", null, isActive: false);
        Assert.Null(credential.RevokedAt);
        credential.Update("reader", null, isActive: true);
        Assert.True(credential.IsActive);

        credential.Revoke();
        var revokedAt = credential.RevokedAt;
        Assert.NotNull(revokedAt);
        Assert.Throws<InvalidOperationException>(() => credential.Update("reader", null, isActive: true));
        Assert.Throws<InvalidOperationException>(() => credential.SetActive(true));
        credential.Update("reader-renamed", null, isActive: false);
        credential.Revoke();
        Assert.Equal(revokedAt, credential.RevokedAt);
        Assert.False(credential.CanAuthenticate(OpenApiCredential.TargetTypeApplication, OpenApiCredential.ScopeApplicationRead));
    }
}
