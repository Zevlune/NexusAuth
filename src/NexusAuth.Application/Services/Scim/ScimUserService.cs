using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Application.Services.Sessions;
using System.Text.Json;

namespace NexusAuth.Application.Services.Scim;

public class ScimUserService(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    ISsoSessionService sessionService) : IScimUserService
{
    private const int MaximumPageSize = 200;

    public async Task<ScimUser?> FindAsync(Guid id, CancellationToken ct = default)
    {
        var user = await userRepository.FindByIdAsync(id, ct);
        // 中文注释：系统账号（管理员）不通过 SCIM 暴露，视同不存在；列表查询由仓储同样排除。
        return user is null || user.IsSystemAccount ? null : Map(user);
    }

    public async Task<ScimUserList> ListAsync(string? filter, int startIndex, int count, CancellationToken ct = default)
    {
        var criteria = ParseFilter(filter);
        startIndex = Math.Max(1, startIndex);
        count = Math.Clamp(count, 0, MaximumPageSize);
        var (items, total) = await userRepository.GetScimPagedAsync(
            criteria.UserName, criteria.ExternalId, criteria.Active, criteria.Email, startIndex, count, ct);
        return new ScimUserList(total, startIndex, items.Select(Map).ToArray());
    }

    public async Task<ScimUser> CreateAsync(ScimUserInput input, CancellationToken ct = default)
    {
        var next = Normalize(input);
        var username = Required(next.UserName, "userName");
        Validate(next);
        await EnsureUniqueAsync(next, null, ct);
        var user = User.Create(username, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), username, next.Email, next.PhoneNumber);
        Apply(user, next, username, next.Active ?? true);
        await userRepository.AddAsync(user, ct);
        return Map(user);
    }

    /// <summary>
    /// Replaces a user. Returns null when the user does not exist; throws
    /// <see cref="ScimOperationException"/> for system accounts (403) and ETag mismatches (412).
    /// </summary>
    public async Task<ScimUser?> ReplaceAsync(Guid id, ScimUserInput input, string? expectedVersion, CancellationToken ct = default)
    {
        var user = await FindMutableUserAsync(id, expectedVersion, ct);
        if (user is null) return null;
        var next = Normalize(input);
        // 中文注释：PUT 省略 active 时保留当前状态，避免把已禁用的用户重新启用。
        return await SaveAsync(user, next with { Active = next.Active ?? user.IsActive }, ct);
    }

    public async Task<ScimUser?> PatchAsync(Guid id, IReadOnlyList<ScimPatchOperation> operations, string? expectedVersion, CancellationToken ct = default)
    {
        var user = await FindMutableUserAsync(id, expectedVersion, ct);
        if (user is null) return null;
        var next = ToInput(user);
        foreach (var operation in operations)
            next = ApplyPatch(next, operation);
        return await SaveAsync(user, Normalize(next), ct);
    }

    public async Task<bool> DeleteAsync(Guid id, string? expectedVersion, CancellationToken ct = default)
    {
        var user = await FindMutableUserAsync(id, expectedVersion, ct);
        if (user is null) return false;

        // 中文注释：与 Workbench 删除顺序一致，先持久化禁用和令牌失效，部分失败也不会留下可用账号。
        user.UpdateScimProfile(user.Username, user.Nickname, false, user.ExternalId, user.Email, user.PhoneNumber,
            user.GivenName, user.FamilyName, user.MiddleName, user.HonorificPrefix, user.HonorificSuffix, user.ProfileUrl,
            user.Title, user.UserType, user.PreferredLanguage, user.Locale, user.Timezone);
        user.InvalidateTokens(DateTimeOffset.UtcNow);
        await userRepository.UpdateAsync(user, ct);
        await RevokeSessionsAsync(user.Id, ct);
        await userRepository.DeleteAsync(user, ct);
        return true;
    }

    private async Task<User?> FindMutableUserAsync(Guid id, string? expectedVersion, CancellationToken ct)
    {
        var user = await userRepository.FindByIdAsync(id, ct);
        if (user is null) return null;
        if (user.IsSystemAccount)
            throw ScimOperationException.Forbidden("System accounts cannot be modified or deleted through SCIM.");
        if (!MatchesVersion(user, expectedVersion))
            throw ScimOperationException.PreconditionFailed();
        return user;
    }

    private async Task<ScimUser> SaveAsync(User user, ScimUserInput next, CancellationToken ct)
    {
        var username = Required(next.UserName, "userName");
        Validate(next);
        await EnsureUniqueAsync(next, user.Id, ct);

        // 中文注释：邮箱/手机号可用于找回密码和登录，变更时与自助换绑邮箱一致：令牌失效并撤销会话与刷新令牌。
        var contactChanged = !string.Equals(user.Email, next.Email, StringComparison.Ordinal)
            || !string.Equals(user.PhoneNumber, next.PhoneNumber, StringComparison.Ordinal);
        Apply(user, next, username, next.Active ?? user.IsActive);
        var revokeSessions = !user.IsActive || contactChanged;
        if (revokeSessions)
            user.InvalidateTokens(DateTimeOffset.UtcNow);
        await userRepository.UpdateAsync(user, ct);
        if (revokeSessions)
            await RevokeSessionsAsync(user.Id, ct);
        return Map(user);
    }

    private async Task EnsureUniqueAsync(ScimUserInput input, Guid? currentId, CancellationToken ct)
    {
        var sameUsername = await userRepository.FindByUsernameAsync(input.UserName!, ct);
        if (sameUsername is not null && sameUsername.Id != currentId) throw ScimOperationException.Uniqueness("userName already exists.");
        if (input.ExternalId is not null)
        {
            var sameExternalId = await userRepository.FindByExternalIdAsync(input.ExternalId, ct);
            if (sameExternalId is not null && sameExternalId.Id != currentId) throw ScimOperationException.Uniqueness("externalId already exists for this provisioning connection.");
        }
        if (input.Email is not null)
        {
            var sameEmail = await userRepository.FindByEmailAsync(input.Email, ct);
            if (sameEmail is not null && sameEmail.Id != currentId) throw ScimOperationException.Uniqueness("email already exists.");
        }
        if (input.PhoneNumber is not null)
        {
            var samePhoneNumber = await userRepository.FindByPhoneNumberAsync(input.PhoneNumber, ct);
            if (samePhoneNumber is not null && samePhoneNumber.Id != currentId) throw ScimOperationException.Uniqueness("phoneNumber already exists.");
        }
    }

    private static void Apply(User user, ScimUserInput input, string username, bool isActive) => user.UpdateScimProfile(
        username, input.GivenName ?? input.FamilyName ?? username, isActive, input.ExternalId, input.Email, input.PhoneNumber,
        input.GivenName, input.FamilyName, input.MiddleName, input.HonorificPrefix, input.HonorificSuffix, input.ProfileUrl,
        input.Title, input.UserType, input.PreferredLanguage, input.Locale, input.Timezone);

    private async Task RevokeSessionsAsync(Guid userId, CancellationToken ct)
    {
        await refreshTokenRepository.RevokeAllForUserAsync(userId, ct);
        await sessionService.RevokeAllForUserAsync(userId, ct);
    }

    /// <summary>
    /// Applies the same normalization as <see cref="User.UpdateScimProfile"/> so uniqueness checks,
    /// length checks and change detection see the values that will actually be stored.
    /// </summary>
    private static ScimUserInput Normalize(ScimUserInput input) => input with
    {
        UserName = Trim(input.UserName), ExternalId = Trim(input.ExternalId), Email = Trim(input.Email)?.ToLowerInvariant(),
        PhoneNumber = Trim(input.PhoneNumber), GivenName = Trim(input.GivenName), FamilyName = Trim(input.FamilyName),
        MiddleName = Trim(input.MiddleName), HonorificPrefix = Trim(input.HonorificPrefix), HonorificSuffix = Trim(input.HonorificSuffix),
        ProfileUrl = Trim(input.ProfileUrl), Title = Trim(input.Title), UserType = Trim(input.UserType),
        PreferredLanguage = Trim(input.PreferredLanguage), Locale = Trim(input.Locale), Timezone = Trim(input.Timezone)
    };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // 中文注释：长度上限与 production-init.sql 中 nexusauth.users 的列宽一致，超长输入返回 SCIM 400 而不是数据库 500。
    private static void Validate(ScimUserInput input)
    {
        MaxLength(input.UserName, "userName", 100);
        MaxLength(input.ExternalId, "externalId", 256);
        MaxLength(input.Email, "emails.value", 256);
        MaxLength(input.PhoneNumber, "phoneNumbers.value", 20);
        MaxLength(input.GivenName, "name.givenName", 100);
        MaxLength(input.FamilyName, "name.familyName", 100);
        MaxLength(input.MiddleName, "name.middleName", 100);
        MaxLength(input.HonorificPrefix, "name.honorificPrefix", 50);
        MaxLength(input.HonorificSuffix, "name.honorificSuffix", 50);
        MaxLength(input.ProfileUrl, "profileUrl", 2048);
        MaxLength(input.Title, "title", 256);
        MaxLength(input.UserType, "userType", 100);
        MaxLength(input.PreferredLanguage, "preferredLanguage", 35);
        MaxLength(input.Locale, "locale", 35);
        MaxLength(input.Timezone, "timezone", 100);
    }

    private static void MaxLength(string? value, string attribute, int maxLength)
    {
        if (value is not null && value.Length > maxLength)
            throw ScimOperationException.InvalidValue($"{attribute} must be at most {maxLength} characters.");
    }

    private static ScimUserInput ToInput(User user) => new(user.Username, user.ExternalId, user.IsActive, user.Email, user.PhoneNumber,
        user.GivenName, user.FamilyName, user.MiddleName, user.HonorificPrefix, user.HonorificSuffix, user.ProfileUrl, user.Title,
        user.UserType, user.PreferredLanguage, user.Locale, user.Timezone);

    private static ScimUser Map(User user) => new(user.Id, user.Username, user.ExternalId, user.IsActive, user.Email, user.PhoneNumber,
        user.GivenName, user.FamilyName, user.MiddleName, user.HonorificPrefix, user.HonorificSuffix, user.ProfileUrl, user.Title,
        user.UserType, user.PreferredLanguage, user.Locale, user.Timezone, user.CreatedAt, user.UpdatedAt);

    public static string Version(ScimUser user) => Version(user.UpdatedAt);
    private static bool MatchesVersion(User user, string? expected) => string.IsNullOrWhiteSpace(expected) || expected == "*" || expected == Version(user.UpdatedAt);
    private static string Version(DateTimeOffset updatedAt)
    {
        var postgresTicks = updatedAt.UtcTicks - updatedAt.UtcTicks % TimeSpan.TicksPerMicrosecond;
        return $"\"{postgresTicks}\"";
    }
    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException($"{name} is required.");

    private static (string? UserName, string? ExternalId, bool? Active, string? Email) ParseFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return default;
        var match = System.Text.RegularExpressions.Regex.Match(filter.Trim(), "^(userName|externalId|active|emails\\.value)\\s+eq\\s+(?:\\\"([^\\\"]*)\\\"|(true|false))$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) throw ScimOperationException.InvalidFilter("Only userName, externalId, active, and emails.value equality filters are supported.");
        var attribute = match.Groups[1].Value;
        var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        if (attribute.Equals("active", StringComparison.OrdinalIgnoreCase))
            return bool.TryParse(value, out var active)
                ? (null, null, active, null)
                : throw ScimOperationException.InvalidFilter("The active filter requires true or false.");
        // 中文注释：空值过滤此前会被仓储忽略并返回全部用户，这里直接拒绝。
        if (string.IsNullOrWhiteSpace(value))
            throw ScimOperationException.InvalidFilter($"The {attribute} filter value must not be empty.");
        return attribute.ToLowerInvariant() switch
        {
            "username" => (value, null, null, null), "externalid" => (null, value, null, null), _ => (null, null, null, value)
        };
    }

    private static ScimUserInput ApplyPatch(ScimUserInput input, ScimPatchOperation operation)
    {
        var path = operation.Path?.Trim().ToLowerInvariant();
        var remove = string.Equals(operation.Op, "remove", StringComparison.OrdinalIgnoreCase);
        if (!remove && !string.Equals(operation.Op, "add", StringComparison.OrdinalIgnoreCase) && !string.Equals(operation.Op, "replace", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PATCH op must be add, replace, or remove.");

        if (path is null)
            return ApplyPathlessPatch(input, operation, remove);

        var value = remove ? null : StringValue(operation.Value);
        return path switch
        {
            // 中文注释：remove active 表示“未授权启用”，按禁用处理（失败即关闭），不再把用户重新启用。
            "username" => input with { UserName = value }, "externalid" => input with { ExternalId = value }, "active" => input with { Active = !remove && BooleanValue(operation.Value) },
            "emails.value" => input with { Email = value }, "phonenumbers.value" => input with { PhoneNumber = value }, "name.givenname" => input with { GivenName = value }, "name.familyname" => input with { FamilyName = value },
            "name.middlename" => input with { MiddleName = value }, "name.honorificprefix" => input with { HonorificPrefix = value }, "name.honorificsuffix" => input with { HonorificSuffix = value },
            "profileurl" => input with { ProfileUrl = value }, "title" => input with { Title = value }, "usertype" => input with { UserType = value }, "preferredlanguage" => input with { PreferredLanguage = value }, "locale" => input with { Locale = value }, "timezone" => input with { Timezone = value },
            _ => throw new ArgumentException("Unsupported PATCH path.")
        };
    }

    private static ScimUserInput ApplyPathlessPatch(ScimUserInput input, ScimPatchOperation operation, bool remove)
    {
        if (remove || operation.Value is not { ValueKind: JsonValueKind.Object } value)
            throw new ArgumentException("PATCH operations without a path require an object value.");

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object && property.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in property.Value.EnumerateObject())
                    input = ApplyPatch(input, new ScimPatchOperation(operation.Op, $"name.{child.Name}", child.Value.Clone()));
                continue;
            }

            var path = property.Name.Equals("emails", StringComparison.OrdinalIgnoreCase) ? "emails.value"
                : property.Name.Equals("phoneNumbers", StringComparison.OrdinalIgnoreCase) ? "phoneNumbers.value"
                : property.Name;
            var propertyValue = path.EndsWith(".value", StringComparison.Ordinal)
                ? FirstScimValue(property.Value)
                : property.Value.Clone();
            input = ApplyPatch(input, new ScimPatchOperation(operation.Op, path, propertyValue));
        }

        return input;
    }

    private static JsonElement? FirstScimValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("emails and phoneNumbers require an array value.");

        JsonElement? first = null;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("emails and phoneNumbers items must be objects.");
            if (!item.TryGetProperty("value", out var itemValue))
                continue;
            first ??= itemValue.Clone();
            if (item.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                return itemValue.Clone();
        }
        return first;
    }

    private static bool BooleanValue(JsonElement? value)
    {
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.Value.GetString(), out var parsed) => parsed,
            _ => throw new ArgumentException("active requires a boolean value.")
        };
    }

    private static string? StringValue(JsonElement? value)
    {
        return value?.ValueKind switch
        {
            null or JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.Value.GetString(),
            _ => value.Value.GetRawText()
        };
    }
}
