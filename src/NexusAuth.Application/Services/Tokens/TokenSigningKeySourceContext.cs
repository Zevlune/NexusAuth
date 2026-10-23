namespace NexusAuth.Application.Services.Tokens;

public sealed record TokenSigningKeySourceContext(
    TokenSigningKeyOptions Options,
    string ContentRootPath,
    bool IsDevelopment);
