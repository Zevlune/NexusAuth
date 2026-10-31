using System.Net;
using System.Text.Json;

namespace NexusAuth.Extension;

internal static class NexusAuthProtocolResponse
{
    public static NexusAuthProtocolException InvalidResponse(
        HttpStatusCode statusCode,
        Exception? innerException = null)
    {
        return new NexusAuthProtocolException("invalid_response", statusCode, innerException);
    }

    public static void ThrowIfOAuthError(string body, HttpStatusCode statusCode)
    {
        if (TryReadOAuthError(body, out var error))
            throw new NexusAuthProtocolException(error, statusCode);
    }

    public static NexusAuthProtocolException FromResponse(
        string body,
        HttpStatusCode statusCode)
    {
        return TryReadOAuthError(body, out var error)
            ? new NexusAuthProtocolException(error, statusCode)
            : new NexusAuthProtocolException("http_error", statusCode);
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
