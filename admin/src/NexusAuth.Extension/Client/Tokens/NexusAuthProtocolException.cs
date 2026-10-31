using System.Net;

namespace NexusAuth.Extension;

/// <summary>授权服务器返回 OAuth 错误或无效响应；消息不包含令牌和响应正文。</summary>
public sealed class NexusAuthProtocolException(string error, HttpStatusCode statusCode, Exception? innerException = null)
    : Exception($"The authorization server returned protocol error '{error}' (HTTP {(int)statusCode}).", innerException)
{
    /// <summary>OAuth error 代码，或 invalid_response / http_error。</summary>
    public string Error { get; } = error;

    /// <summary>HTTP 状态码。</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}
