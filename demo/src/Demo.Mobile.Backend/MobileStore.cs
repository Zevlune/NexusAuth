using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace Demo.Mobile.Backend;

// 单进程演示存储；到期自动清理。生产替换为带 TTL、原子消费和分布式锁的共享存储。
public sealed class MobileStore(IMemoryCache cache)
{
    private readonly object _gate = new();
    public static string RandomToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Key(string kind, string token) => kind + Challenge(token);
    public string AddFlow(LoginFlow flow)
    {
        var state = RandomToken();
        cache.Set(Key("flow:", state), flow, TimeSpan.FromMinutes(5));
        return state;
    }
    public LoginFlow? ConsumeFlow(string state)
    {
        lock (_gate)
        {
            var key = Key("flow:", state);
            var value = cache.Get<LoginFlow>(key);
            cache.Remove(key);
            return value;
        }
    }
    public string AddTicket(LoginTicket ticket)
    {
        var token = RandomToken();
        cache.Set(Key("ticket:", token), ticket, TimeSpan.FromSeconds(60));
        return token;
    }
    public MobileSession? Exchange(string ticket, string verifier, out string? sessionToken)
    {
        sessionToken = null;
        lock (_gate)
        {
            var key = Key("ticket:", ticket);
            var value = cache.Get<LoginTicket>(key);
            if (value is null || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(value.Challenge), Encoding.ASCII.GetBytes(Challenge(verifier))))
                return null;
            cache.Remove(key);
            sessionToken = RandomToken();
            cache.Set(Key("session:", sessionToken), value.Session, value.Session.ExpiresAt);
            return value.Session;
        }
    }
    public MobileSession? FindSession(string token) => cache.Get<MobileSession>(Key("session:", token));
    public void RemoveSession(string token) => cache.Remove(Key("session:", token));
}

public sealed record LoginFlow(string Verifier, string Nonce, string AppState, string AppChallenge);
public sealed record LoginTicket(string Challenge, MobileSession Session);
public sealed class MobileSession(TokenSet tokens, UserInfo user)
{
    public TokenSet Tokens { get; set; } = tokens;
    public UserInfo User { get; set; } = user;
    public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddHours(8);
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool Closed { get; set; }
}
