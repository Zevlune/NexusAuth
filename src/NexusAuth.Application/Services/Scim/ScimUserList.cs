namespace NexusAuth.Application.Services.Scim;

public sealed record ScimUserList(int TotalResults, int StartIndex, IReadOnlyList<ScimUser> Resources);
