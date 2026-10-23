namespace NexusAuth.Application.Services.Tokens;

public interface ITokenSigningKeySource : ISingletonDependency
{
    string Name { get; }

    TokenSigningKeyMaterial Load(TokenSigningKeySourceContext context);
}
