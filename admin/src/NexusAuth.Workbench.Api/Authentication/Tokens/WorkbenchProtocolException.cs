using System.Net;

namespace NexusAuth.Workbench.Api.Authentication.Tokens;

public sealed class WorkbenchProtocolException(string error, HttpStatusCode statusCode, Exception? innerException = null)
    : Exception($"The authorization server returned protocol error '{error}' (HTTP {(int)statusCode}).", innerException)
{
    public string Error { get; } = error;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
