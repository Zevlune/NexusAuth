namespace NexusAuth.Application.Services.Scim;

/// <summary>
/// A SCIM protocol error (RFC 7644 §3.12) carrying the HTTP status and optional scimType
/// the controller must return, so unrelated failures are not all reported as 409 uniqueness.
/// </summary>
public sealed class ScimOperationException(int status, string? scimType, string detail) : Exception(detail)
{
    public int Status { get; } = status;

    public string? ScimType { get; } = scimType;

    public static ScimOperationException InvalidValue(string detail) => new(400, "invalidValue", detail);

    public static ScimOperationException InvalidFilter(string detail) => new(400, "invalidFilter", detail);

    public static ScimOperationException Uniqueness(string detail) => new(409, "uniqueness", detail);

    public static ScimOperationException Forbidden(string detail) => new(403, null, detail);

    public static ScimOperationException PreconditionFailed() => new(412, null, "The ETag does not match the current version of the user.");
}
