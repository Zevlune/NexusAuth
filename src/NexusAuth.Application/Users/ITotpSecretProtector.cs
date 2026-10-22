namespace NexusAuth.Application.Users;

public interface ITotpSecretProtector : ISingletonDependency
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
