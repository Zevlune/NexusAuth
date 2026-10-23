namespace NexusAuth.Host.TokenEndpoint;

public record TokenGrantContext(HttpContext HttpContext, TokenRequest Request);
