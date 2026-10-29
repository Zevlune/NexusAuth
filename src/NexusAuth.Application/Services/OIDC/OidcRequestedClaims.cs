using System.Numerics;

namespace NexusAuth.Application.Services.OIDC;

public sealed class OidcRequestedClaims
{
    public static readonly OidcRequestedClaims Empty = new(
        new Dictionary<string, OidcClaimRequest>(StringComparer.Ordinal),
        new Dictionary<string, OidcClaimRequest>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, OidcClaimRequest> IdTokenClaimRequests { get; }

    public IReadOnlyDictionary<string, OidcClaimRequest> UserInfoClaimRequests { get; }

    public HashSet<string> IdTokenClaims => IdTokenClaimRequests.Keys.ToHashSet(StringComparer.Ordinal);

    public HashSet<string> UserInfoClaims => UserInfoClaimRequests.Keys.ToHashSet(StringComparer.Ordinal);

    public bool HasExplicitRequests => IdTokenClaims.Count > 0 || UserInfoClaims.Count > 0;

    private OidcRequestedClaims(
        IReadOnlyDictionary<string, OidcClaimRequest> idTokenClaimRequests,
        IReadOnlyDictionary<string, OidcClaimRequest> userInfoClaimRequests)
    {
        IdTokenClaimRequests = idTokenClaimRequests;
        UserInfoClaimRequests = userInfoClaimRequests;
    }

    /// <summary>
    /// Creates explicit claim requests while preserving each claim request object.
    /// </summary>
    public static OidcRequestedClaims CreateRequests(
        IReadOnlyDictionary<string, OidcClaimRequest>? idTokenClaims = null,
        IReadOnlyDictionary<string, OidcClaimRequest>? userInfoClaims = null)
    {
        return new OidcRequestedClaims(
            CopyRequests(idTokenClaims),
            CopyRequests(userInfoClaims));
    }

    /// <summary>
    /// </summary>
    public static OidcRequestedClaims Create(IEnumerable<string>? idTokenClaims = null, IEnumerable<string>? userInfoClaims = null)
    {
        var idTokenMap = (idTokenClaims ?? [])
            .ToDictionary(claim => claim, _ => OidcClaimRequest.Empty, StringComparer.Ordinal);
        var userInfoMap = (userInfoClaims ?? [])
            .ToDictionary(claim => claim, _ => OidcClaimRequest.Empty, StringComparer.Ordinal);

        return new OidcRequestedClaims(
            idTokenMap,
            userInfoMap);
    }

    /// <summary>
    /// </summary>
    public static OidcRequestedClaims Parse(string? claimsJson)
    {
        if (string.IsNullOrWhiteSpace(claimsJson))
            return Empty;

        using var document = JsonDocument.Parse(claimsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("OIDC claims parameter must be a JSON object.");

        return new OidcRequestedClaims(
            ParseClaimSection(document.RootElement, "id_token"),
            ParseClaimSection(document.RootElement, "userinfo"));
    }

    /// <summary>
    /// </summary>
    public bool RequestsIdTokenClaim(string claimName)
    {
        return IdTokenClaimRequests.ContainsKey(claimName);
    }

    /// <summary>
    /// </summary>
    public bool RequestsUserInfoClaim(string claimName)
    {
        return UserInfoClaimRequests.ContainsKey(claimName);
    }

    /// <summary>
    /// </summary>
    public OidcClaimRequest? GetClaimRequest(string claimName)
    {
        if (IdTokenClaimRequests.TryGetValue(claimName, out var idTokenRequest))
            return idTokenRequest;

        return UserInfoClaimRequests.TryGetValue(claimName, out var userInfoRequest) ? userInfoRequest : null;
    }

    /// <summary>
    /// </summary>
    public bool RequestsClaim(string claimName)
    {
        return RequestsIdTokenClaim(claimName) || RequestsUserInfoClaim(claimName);
    }

    private static IReadOnlyDictionary<string, OidcClaimRequest> ParseClaimSection(JsonElement root, string sectionName)
    {
        JsonElement? section = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, sectionName, StringComparison.Ordinal))
                continue;

            if (section.HasValue)
                throw new InvalidOperationException($"OIDC claims parameter section '{sectionName}' must not appear more than once.");

            section = property.Value;
        }

        var claims = new Dictionary<string, OidcClaimRequest>(StringComparer.Ordinal);
        if (!section.HasValue)
            return claims;

        if (section.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"OIDC claims parameter section '{sectionName}' must be an object.");

        foreach (var property in section.Value.EnumerateObject())
        {
            if (!claims.TryAdd(property.Name, OidcClaimRequest.Parse(property.Value)))
                throw new InvalidOperationException(
                    $"OIDC claim '{property.Name}' must not appear more than once in the '{sectionName}' section.");
        }

        return claims;
    }

    private static IReadOnlyDictionary<string, OidcClaimRequest> CopyRequests(
        IReadOnlyDictionary<string, OidcClaimRequest>? requests)
    {
        return requests is null
            ? new Dictionary<string, OidcClaimRequest>(StringComparer.Ordinal)
            : requests.ToDictionary(
                request => request.Key,
                request => request.Value,
                StringComparer.Ordinal);
    }
}

public enum OidcClaimScalarKind
{
    String,
    Boolean,
    Number,
}

public sealed record OidcClaimRequest(bool? Essential, string? Value, IReadOnlyList<string> Values)
{
    public static readonly OidcClaimRequest Empty = new(null, null, []);

    public OidcClaimScalarKind? ValueKind { get; init; }

    public OidcClaimScalarKind? ValuesKind { get; init; }

    public static OidcClaimRequest Parse(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Empty;

        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("OIDC claim request entries must be objects or null.");

        bool? essential = null;
        string? value = null;
        OidcClaimScalarKind? valueKind = null;
        List<string> values = [];
        OidcClaimScalarKind? valuesKind = null;
        var hasEssential = false;
        var hasValue = false;
        var hasValues = false;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "essential":
                    if (hasEssential)
                        throw new InvalidOperationException("OIDC claim request member 'essential' must not appear more than once.");

                    hasEssential = true;
                    if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                        throw new InvalidOperationException("OIDC claim request 'essential' must be a boolean.");

                    essential = property.Value.GetBoolean();
                    break;

                case "value":
                    if (hasValue)
                        throw new InvalidOperationException("OIDC claim request member 'value' must not appear more than once.");

                    hasValue = true;
                    (value, valueKind) = ParseScalar(property.Value, "value");
                    break;

                case "values":
                    if (hasValues)
                        throw new InvalidOperationException("OIDC claim request member 'values' must not appear more than once.");

                    hasValues = true;
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("OIDC claim request 'values' must be an array.");

                    foreach (var item in property.Value.EnumerateArray())
                    {
                        var (requestedValue, requestedValueKind) = ParseScalar(item, "values");
                        if (valuesKind.HasValue && valuesKind.Value != requestedValueKind)
                        {
                            throw new InvalidOperationException(
                                "OIDC claim request 'values' members must use one scalar type.");
                        }

                        valuesKind = requestedValueKind;
                        values.Add(requestedValue);
                    }

                    if (values.Count == 0)
                        throw new InvalidOperationException("OIDC claim request 'values' must contain at least one scalar.");

                    break;
            }
        }

        if (hasValue && hasValues)
            throw new InvalidOperationException("OIDC claim request must not contain both 'value' and 'values'.");

        return new OidcClaimRequest(essential, value, values)
        {
            ValueKind = valueKind,
            ValuesKind = valuesKind,
        };
    }

    public bool Matches(
        string? actualValue,
        OidcClaimScalarKind actualKind = OidcClaimScalarKind.String)
    {
        if (string.IsNullOrWhiteSpace(actualValue))
            return !IsConstrained;

        if (!string.IsNullOrWhiteSpace(Value)
            && !MatchesScalar(Value, ValueKind, actualValue, actualKind))
            return false;

        if (Values.Count > 0
            && !Values.Any(value => MatchesScalar(value, ValuesKind, actualValue, actualKind)))
            return false;

        return true;
    }

    public bool IsConstrained => !string.IsNullOrWhiteSpace(Value) || Values.Count > 0;

    private static bool MatchesScalar(
        string requestedValue,
        OidcClaimScalarKind? requestedKind,
        string actualValue,
        OidcClaimScalarKind actualKind)
    {
        if ((requestedKind ?? OidcClaimScalarKind.String) != actualKind)
            return false;

        if (actualKind == OidcClaimScalarKind.Number)
            return MatchesJsonNumber(requestedValue, actualValue);

        return string.Equals(requestedValue, actualValue, StringComparison.Ordinal);
    }

    private static bool MatchesJsonNumber(string requestedValue, string actualValue)
    {
        if (!TryNormalizeJsonNumber(requestedValue, out var requestedNumber)
            || !TryNormalizeJsonNumber(actualValue, out var actualNumber))
        {
            return string.Equals(requestedValue, actualValue, StringComparison.Ordinal);
        }

        return requestedNumber.Sign == actualNumber.Sign
            && string.Equals(requestedNumber.Digits, actualNumber.Digits, StringComparison.Ordinal)
            && requestedNumber.Exponent == actualNumber.Exponent;
    }

    private static bool TryNormalizeJsonNumber(string value, out NormalizedJsonNumber normalized)
    {
        normalized = new NormalizedJsonNumber(0, string.Empty, BigInteger.Zero);
        if (string.IsNullOrEmpty(value))
            return false;

        var index = 0;
        var negative = false;
        if (value[index] is '-' or '+')
        {
            negative = value[index] == '-';
            index++;
            if (index == value.Length)
                return false;
        }

        var integerStart = index;
        while (index < value.Length && IsAsciiDigit(value[index]))
            index++;
        if (index == integerStart)
            return false;

        var integerDigits = value.AsSpan(integerStart, index - integerStart);
        var fractionalDigits = ReadOnlySpan<char>.Empty;
        if (index < value.Length && value[index] == '.')
        {
            index++;
            var fractionStart = index;
            while (index < value.Length && IsAsciiDigit(value[index]))
                index++;
            if (index == fractionStart)
                return false;

            fractionalDigits = value.AsSpan(fractionStart, index - fractionStart);
        }

        var exponent = BigInteger.Zero;
        if (index < value.Length && value[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = false;
            if (index < value.Length && value[index] is '-' or '+')
            {
                exponentNegative = value[index] == '-';
                index++;
            }

            var exponentStart = index;
            while (index < value.Length && IsAsciiDigit(value[index]))
            {
                exponent = exponent * 10 + (value[index] - '0');
                index++;
            }

            if (index == exponentStart)
                return false;
            if (exponentNegative)
                exponent = -exponent;
        }

        if (index != value.Length)
            return false;

        var digits = string.Concat(integerDigits, fractionalDigits).TrimStart('0');
        if (digits.Length == 0)
        {
            normalized = new NormalizedJsonNumber(0, "0", BigInteger.Zero);
            return true;
        }

        var trailingZeros = 0;
        while (digits[digits.Length - trailingZeros - 1] == '0')
            trailingZeros++;
        if (trailingZeros > 0)
            digits = digits[..^trailingZeros];

        normalized = new NormalizedJsonNumber(
            negative ? -1 : 1,
            digits,
            exponent - fractionalDigits.Length + trailingZeros);
        return true;
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private readonly record struct NormalizedJsonNumber(int Sign, string Digits, BigInteger Exponent);

    private static (string Value, OidcClaimScalarKind Kind) ParseScalar(JsonElement element, string memberName)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"OIDC claim request '{memberName}' must not be empty.");

            return (value, OidcClaimScalarKind.String);
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return (element.GetBoolean() ? "true" : "false", OidcClaimScalarKind.Boolean);

        if (element.ValueKind == JsonValueKind.Number)
            return (element.GetRawText(), OidcClaimScalarKind.Number);

        throw new InvalidOperationException(
            $"OIDC claim request '{memberName}' must be a string, boolean, or number scalar.");
    }
}
