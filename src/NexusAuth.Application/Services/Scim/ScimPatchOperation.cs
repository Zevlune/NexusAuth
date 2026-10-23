using System.Text.Json;

namespace NexusAuth.Application.Services.Scim;

public sealed record ScimPatchOperation(string Op, string? Path, JsonElement? Value);
