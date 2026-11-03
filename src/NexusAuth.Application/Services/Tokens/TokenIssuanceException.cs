namespace NexusAuth.Application.Services.Tokens;

/// <summary>
/// An expected reason the grant cannot be turned into tokens (the user is gone
/// or inactive, the scopes span several audiences, the stored claims request is
/// no longer valid). Token endpoint strategies map only this type to an OAuth
/// error; any other exception is a server fault and must not be reported to the
/// client as invalid_grant or invalid_scope.
/// </summary>
public sealed class TokenIssuanceException : InvalidOperationException
{
    public TokenIssuanceException(string message) : base(message)
    {
    }

    public TokenIssuanceException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
