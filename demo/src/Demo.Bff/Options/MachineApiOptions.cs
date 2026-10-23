namespace Demo.Bff.Options;

/// <summary>
/// /api/m2m/profile 的访问约束。
/// 仅校验 aud 不够：任何携带 demo-bff-api 资源 scope 的令牌都会拿到相同的 aud，
/// 因此还需要限定 scope 与允许调用的 client_id。
/// </summary>
public class MachineApiOptions
{
    public const string PolicyName = "MachineApi";

    public string RequiredScope { get; set; } = "demo-bff-api";

    /// <summary>
    /// 允许调用的 client_id 白名单，在 appsettings.json 的 MachineApi:AllowedClientIds 中配置；
    /// 未配置时拒绝所有调用方。
    /// </summary>
    public string[] AllowedClientIds { get; set; } = [];
}
