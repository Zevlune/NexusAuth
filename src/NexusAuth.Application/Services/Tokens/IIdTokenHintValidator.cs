namespace NexusAuth.Application.Services.Tokens;

public interface IIdTokenHintValidator : ISingletonDependency
{
    IdTokenHintValidationResult Validate(string idTokenHint);
}
