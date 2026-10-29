using System.Net;
using System.Text.Json;

namespace NexusAuth.Workbench.Api.Authentication.Tokens;

internal static class WorkbenchProtocolResponse
{
    public static WorkbenchProtocolException InvalidResponse(
        HttpStatusCode statusCode,
        Exception? innerException = null)
    {
        return new WorkbenchProtocolException("invalid_response", statusCode, innerException);
    }

    public static void ThrowIfOAuthError(string body, HttpStatusCode statusCode)
    {
        if (TryReadOAuthError(body, out var error))
            throw new WorkbenchProtocolException(error, statusCode);
    }

    public static WorkbenchProtocolException FromResponse(
        string body,
        HttpStatusCode statusCode)
    {
        return TryReadOAuthError(body, out var error)
            ? new WorkbenchProtocolException(error, statusCode)
            : new WorkbenchProtocolException("http_error", statusCode);
    }

    private static bool TryReadOAuthError(string body, out string error)
    {
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var errorProperty)
                && errorProperty.ValueKind == JsonValueKind.String)
            {
                error = errorProperty.GetString() ?? string.Empty;
                return !string.IsNullOrEmpty(error);
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
