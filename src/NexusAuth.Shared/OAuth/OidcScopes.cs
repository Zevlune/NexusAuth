namespace NexusAuth.Shared.OAuth;

/// <summary>
/// Standard OpenID Connect scopes shared by the authorization server and its clients.
/// </summary>
public static class OidcScopes
{
    public const string OpenId = "openid";
    public const string Profile = "profile";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Address = "address";
    public const string OfflineAccess = "offline_access";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly<string>(
        [OpenId, Profile, Email, Phone, Address, OfflineAccess]);

    public static bool IsStandard(string? scope) =>
        scope is OpenId or Profile or Email or Phone or Address or OfflineAccess;
}
