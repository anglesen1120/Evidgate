using System.Text.Json;
using System.Text.Unicode;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Infrastructure.Parsing;

/// <summary>
/// Parses one rate-card body into a <see cref="UsagePriceCard"/> or the refusals it earned. The body is
/// bounded before anything reads it, the bytes must be valid UTF-8, and every object is read against a closed
/// member list, so an unexpected member is refused rather than ignored. Nothing here echoes a value: a refusal carries
/// the member's path and a stable reason only, a name too long to repeat becomes a fixed placeholder, and the refusal
/// list itself is capped so a body full of offending members still answers in bounded size.
/// <para>
/// Duplicate members are permitted by the document parser on purpose — the walk below refuses them itself, which is the
/// only way to name the member that repeated. Money is parsed from the raw token by
/// <see cref="UsagePriceCardRules.TryParseAmount"/>; no binary floating-point type is involved at any point. Text a JSON
/// string can spell but PostgreSQL cannot store — a lone surrogate or U+0000 — is refused here rather than at the write.
/// </para>
/// </summary>
internal static class UsagePriceCardParser
{
    private const string VersionMember = "rate_card_version";
    private const string PricesMember = "prices";
    private const string MetricMember = "metric";
    private const string UnitPriceMember = "unit_price";
    private const string CurrencyMember = "currency";
    private const string EffectiveFromMember = "effective_from";
    private const string EffectiveToMember = "effective_to";
    private const string IncludedQuotaMember = "included_quota";

    /// <summary>Longest member name a refusal path may repeat; a longer name is replaced by the placeholder.</summary>
    private const int MaxPathSegmentLength = 64;

    /// <summary>The fixed segment a refusal uses when the real name cannot be repeated or is too long.</summary>
    private const string UnknownSegment = "<unknown>";

    private static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        // Refused by the walk below, which can name the repeated member; the parser alone could not.
        AllowDuplicateProperties = true,
    };

    /// <summary>Parses <paramref name="body"/> into a card or its refusals.</summary>
    /// <param name="body">The raw body; its size is the first thing checked.</param>
    internal static UsagePriceCardParseResult Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length > UsagePriceCardRules.MaxBodyBytes)
        {
            return Refuse("/", UsagePriceRefusalReasons.TooLarge);
        }

        // JsonDocument accepts some ill-formed UTF-8 inside strings, so the bytes are validated strictly first.
        if (!Utf8.IsValid(body.Span))
        {
            return Refuse("/", UsagePriceRefusalReasons.InvalidJson);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, Options);
        }
        catch (JsonException)
        {
            return Refuse("/", UsagePriceRefusalReasons.InvalidJson);
        }

        using (document)
        {
            return ReadCard(document.RootElement);
        }
    }

    private static UsagePriceCardParseResult Refuse(string path, string reason) =>
        UsagePriceCardParseResult.Refused(new UsagePriceViolation(path, reason));

    private static UsagePriceCardParseResult ReadCard(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Refuse("/", UsagePriceRefusalReasons.InvalidValue);
        }

        var violations = new ViolationSink();
        FindDuplicate(root, string.Empty, violations);

        var (version, prices) = ReadRoot(root, violations);
        if (version is not { } cardVersion || prices is not { } priceArray)
        {
            return UsagePriceCardParseResult.Refused(violations.ToList());
        }

        var rows = ReadRows(priceArray, violations);

        return violations.Count > 0
            ? UsagePriceCardParseResult.Refused(violations.ToList())
            : new UsagePriceCardParseResult(new UsagePriceCard(cardVersion, rows), []);
    }

    /// <summary>
    /// The refusals of one card, capped: a body can hold thousands of offending members and a response that returned
    /// all of them would be unbounded. Once the cap is reached the walk stops and one named refusal says so.
    /// </summary>
    private sealed class ViolationSink
    {
        private readonly List<UsagePriceViolation> violations = [];

        /// <summary>How many refusals were collected, including the overflow marker once it was added.</summary>
        internal int Count => violations.Count;

        /// <summary>Whether the cap was reached, so the walk must stop reading further members.</summary>
        internal bool Overflowed { get; private set; }

        internal void Add(string path, string reason)
        {
            if (Overflowed)
            {
                return;
            }

            if (violations.Count >= UsagePriceCardRules.MaxViolations)
            {
                Overflowed = true;
                violations.Add(new UsagePriceViolation("/", UsagePriceRefusalReasons.TooManyViolations));
                return;
            }

            violations.Add(new UsagePriceViolation(path, reason));
        }

        internal void AddRange(IEnumerable<UsagePriceViolation> more)
        {
            foreach (var violation in more)
            {
                Add(violation.Path, violation.Reason);
            }
        }

        internal List<UsagePriceViolation> ToList() => violations;
    }

    private static (string? Version, JsonElement? Prices) ReadRoot(
        JsonElement root,
        ViolationSink violations)
    {
        string? version = null;
        JsonElement? prices = null;
        var versionSeen = false;
        var pricesSeen = false;

        foreach (var member in root.EnumerateObject())
        {
            if (violations.Overflowed)
            {
                break;
            }

            if (!TryReadMemberName(member, string.Empty, violations, out var name))
            {
                continue;
            }

            switch (name)
            {
                case VersionMember:
                    versionSeen = true;
                    version = ReadVersion(member.Value, violations);
                    break;
                case PricesMember:
                    pricesSeen = true;
                    prices = member.Value;
                    break;
                default:
                    violations.Add(MemberPath(string.Empty, name), UsagePriceRefusalReasons.UnknownField);
                    break;
            }
        }

        if (!versionSeen)
        {
            violations.Add("/" + VersionMember, UsagePriceRefusalReasons.Required);
        }

        if (!pricesSeen)
        {
            violations.Add("/" + PricesMember, UsagePriceRefusalReasons.Required);
        }

        return (version, prices);
    }

    private static string? ReadVersion(JsonElement value, ViolationSink violations)
    {
        const string path = "/" + VersionMember;

        // Decode first so the length cap keeps the precedence it had: a long version is too long whether or not every
        // character is one the column stores.
        if (value.ValueKind != JsonValueKind.String || !TryDecodeString(value, out var text))
        {
            violations.Add(path, UsagePriceRefusalReasons.InvalidValue);
            return null;
        }

        if (text.Length == 0)
        {
            violations.Add(path, UsagePriceRefusalReasons.Required);
            return null;
        }

        if (CodePointLength(text) > UsagePriceCardRules.MaxVersionLength)
        {
            violations.Add(path, UsagePriceRefusalReasons.TooLong);
            return null;
        }

        if (!PostgresText.IsStorable(text))
        {
            violations.Add(path, UsagePriceRefusalReasons.InvalidValue);
            return null;
        }

        return text;
    }

    private static List<UsagePriceCardRow> ReadRows(JsonElement array, ViolationSink violations)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            violations.Add("/" + PricesMember, UsagePriceRefusalReasons.InvalidValue);
            return [];
        }

        if (array.GetArrayLength() == 0)
        {
            violations.Add("/" + PricesMember, UsagePriceRefusalReasons.Empty);
            return [];
        }

        if (array.GetArrayLength() > UsagePriceCardRules.MaxRows)
        {
            violations.Add("/" + PricesMember, UsagePriceRefusalReasons.TooMany);
            return [];
        }

        var before = violations.Count;
        var rows = new List<UsagePriceCardRow>(array.GetArrayLength());
        var index = 0;

        foreach (var element in array.EnumerateArray())
        {
            if (violations.Overflowed)
            {
                break;
            }

            if (ReadRow(element, "/" + PricesMember + "/" + index, violations) is { } row)
            {
                rows.Add(row);
            }

            index++;
        }

        // The cross-row rules need every row parsed; a row that already failed reports its own reason instead.
        if (violations.Count == before && !violations.Overflowed)
        {
            violations.AddRange(UsagePriceCardRules.ValidateRows(rows));
        }

        return rows;
    }

    private static UsagePriceCardRow? ReadRow(
        JsonElement element,
        string path,
        ViolationSink violations)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            violations.Add(path, UsagePriceRefusalReasons.InvalidValue);
            return null;
        }

        FindDuplicate(element, path, violations);

        var before = violations.Count;
        var (values, seen) = ReadRowMembers(element, path, violations);

        RequireSeen(seen, MetricMember, path, violations);
        RequireSeen(seen, UnitPriceMember, path, violations);
        RequireSeen(seen, CurrencyMember, path, violations);
        RequireSeen(seen, EffectiveFromMember, path, violations);
        var metric = ValidateRow(values, path, violations);

        if (violations.Count > before
            || metric is not { } rowMetric
            || values.UnitPrice is not { } unitPrice
            || values.Currency is not { } currency
            || values.EffectiveFrom is not { } effectiveFrom)
        {
            return null;
        }

        return new UsagePriceCardRow(
            rowMetric,
            unitPrice,
            currency,
            effectiveFrom,
            values.EffectiveTo,
            values.IncludedQuota);
    }

    private static (RowValues Values, HashSet<string> Seen) ReadRowMembers(
        JsonElement element,
        string path,
        ViolationSink violations)
    {
        var values = new RowValues(null, null, null, null, null, null);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in element.EnumerateObject())
        {
            if (violations.Overflowed)
            {
                break;
            }

            if (!TryReadMemberName(member, path, violations, out var name))
            {
                continue;
            }

            seen.Add(name);

            switch (name)
            {
                case MetricMember:
                    values = values with { MetricName = ReadStorableString(member.Value, path + "/" + MetricMember, violations) };
                    break;
                case UnitPriceMember:
                    values = values with { UnitPrice = ReadAmount(member.Value, path + "/" + UnitPriceMember, violations) };
                    break;
                case CurrencyMember:
                    values = values with { Currency = ReadStorableString(member.Value, path + "/" + CurrencyMember, violations) };
                    break;
                case EffectiveFromMember:
                    values = values with
                    {
                        EffectiveFrom = ReadInstant(member.Value, path + "/" + EffectiveFromMember, violations),
                    };
                    break;
                case EffectiveToMember:
                    values = values with
                    {
                        EffectiveTo = ReadNullableInstant(member.Value, path + "/" + EffectiveToMember, violations),
                    };
                    break;
                case IncludedQuotaMember:
                    values = values with
                    {
                        IncludedQuota = ReadNullableAmount(member.Value, path + "/" + IncludedQuotaMember, violations),
                    };
                    break;
                default:
                    violations.Add(MemberPath(path, name), UsagePriceRefusalReasons.UnknownField);
                    break;
            }
        }

        return (values, seen);
    }

    private static UsageMetric? ValidateRow(
        RowValues values,
        string path,
        ViolationSink violations)
    {
        UsageMetric? metric = null;

        if (values.MetricName is not null)
        {
            if (UsagePriceCardRules.TryParseMetric(values.MetricName, out var parsed))
            {
                metric = parsed;
            }
            else
            {
                violations.Add(path + "/" + MetricMember, UsagePriceRefusalReasons.UnknownMetric);
            }
        }

        if (values.Currency is not null && !UsagePriceCardRules.IsCurrency(values.Currency))
        {
            violations.Add(path + "/" + CurrencyMember, UsagePriceRefusalReasons.InvalidValue);
        }

        if (values.EffectiveFrom is { } from && values.EffectiveTo is { } to && to <= from)
        {
            violations.Add(path + "/" + EffectiveToMember, UsagePriceRefusalReasons.InvalidValue);
        }

        return metric;
    }

    /// <summary>
    /// Reads a JSON string only when it is text the database can store: a lone surrogate or a U+0000 has no encoding
    /// the column accepts, and letting it through would turn a refusal into a server error on write.
    /// </summary>
    private static string? ReadStorableString(JsonElement value, string path, ViolationSink violations)
    {
        if (value.ValueKind == JsonValueKind.String
            && TryDecodeString(value, out var text)
            && PostgresText.IsStorable(text))
        {
            return text;
        }

        violations.Add(path, UsagePriceRefusalReasons.InvalidValue);
        return null;
    }

    /// <summary>
    /// Decodes a JSON string, or answers false for an escape that names an unpaired surrogate.
    /// System.Text.Json reports that case by throwing from <c>GetString</c> — the only signal it gives — so the
    /// exception is caught here and becomes the card's own named refusal instead of escaping the parser.
    /// </summary>
    private static bool TryDecodeString(JsonElement value, out string text)
    {
        try
        {
            if (value.GetString() is { } decoded)
            {
                text = decoded;
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // The string's escapes cannot be decoded to UTF-16; the caller reports it as an invalid value.
        }

        text = string.Empty;
        return false;
    }

    private static decimal? ReadAmount(JsonElement value, string path, ViolationSink violations)
    {
        // A JSON number is read from its raw token: converting through a floating-point type would round the money.
        string? token;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (!TryDecodeString(value, out var text) || !PostgresText.IsStorable(text))
                {
                    violations.Add(path, UsagePriceRefusalReasons.InvalidAmount);
                    return null;
                }

                token = text;
                break;
            case JsonValueKind.Number:
                token = value.GetRawText();
                break;
            default:
                violations.Add(path, UsagePriceRefusalReasons.InvalidAmount);
                return null;
        }

        if (!UsagePriceCardRules.TryParseAmount(token, out var amount))
        {
            violations.Add(path, UsagePriceRefusalReasons.InvalidAmount);
            return null;
        }

        return amount;
    }

    private static decimal? ReadNullableAmount(JsonElement value, string path, ViolationSink violations) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadAmount(value, path, violations);

    private static DateTimeOffset? ReadInstant(JsonElement value, string path, ViolationSink violations)
    {
        var text = ReadStorableString(value, path, violations);
        if (text is null)
        {
            return null;
        }

        if (!UsagePriceCardRules.TryParseInstant(text, out var instant))
        {
            violations.Add(path, UsagePriceRefusalReasons.InvalidValue);
            return null;
        }

        // The column stores microseconds; a finer instant would be rounded, which can move a window boundary or even
        // collapse a window to nothing, so the card is refused instead of silently shifted.
        if (!UsagePriceCardRules.IsMicrosecondAligned(instant))
        {
            violations.Add(path, UsagePriceRefusalReasons.InvalidInstant);
            return null;
        }

        return instant;
    }

    private static DateTimeOffset? ReadNullableInstant(
        JsonElement value,
        string path,
        ViolationSink violations) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadInstant(value, path, violations);

    private static void RequireSeen(
        HashSet<string> seen,
        string member,
        string path,
        ViolationSink violations)
    {
        if (!seen.Contains(member))
        {
            violations.Add(path + "/" + member, UsagePriceRefusalReasons.Required);
        }
    }

    /// <summary>
    /// Checks a member name before the walk uses it: a name whose escapes name an unpaired surrogate cannot be
    /// decoded at all, and one that is not text the database can store earns a named refusal either way. The name is
    /// never repeated in that refusal, so a caller-controlled name of any length cannot reach the response.
    /// </summary>
    /// <param name="member">The object member.</param>
    /// <param name="path">The object's JSON pointer.</param>
    /// <param name="violations">The refusals of this card.</param>
    /// <param name="name">The decoded name when the member can be walked.</param>
    /// <returns>Whether the name can be walked as a member.</returns>
    private static bool TryReadMemberName(
        JsonProperty member,
        string path,
        ViolationSink violations,
        out string name)
    {
        try
        {
            name = member.Name;
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json throws when an escape names an unpaired surrogate; not a value the column stores.
            name = string.Empty;
            violations.Add(path + "/" + UnknownSegment, UsagePriceRefusalReasons.InvalidValue);
            return false;
        }

        if (PostgresText.IsStorable(name))
        {
            return true;
        }

        violations.Add(path + "/" + UnknownSegment, UsagePriceRefusalReasons.InvalidValue);
        return false;
    }

    /// <summary>
    /// The pointer of a member: the name when it is a short, storable segment, otherwise a fixed placeholder, so a
    /// refusal never carries a caller-controlled name of any length.
    /// </summary>
    private static string MemberPath(string path, string name) =>
        path + "/" + (PostgresText.IsStorable(name) && CodePointLength(name) <= MaxPathSegmentLength
            ? name
            : UnknownSegment);

    private static void FindDuplicate(JsonElement element, string path, ViolationSink violations)
    {
        HashSet<string>? names = null;

        foreach (var member in element.EnumerateObject())
        {
            if (violations.Overflowed)
            {
                return;
            }

            names ??= new HashSet<string>(StringComparer.Ordinal);

            string name;
            try
            {
                name = member.Name;
            }
            catch (InvalidOperationException)
            {
                // The walk below refuses an undecodable name itself; there is nothing to compare here.
                return;
            }

            if (!names.Add(name))
            {
                violations.Add(MemberPath(path, name), UsagePriceRefusalReasons.DuplicateMember);
                return;
            }
        }
    }

    /// <summary>What one price object declared, before the rules decide whether it is a row.</summary>
    private sealed record RowValues(
        string? MetricName,
        string? Currency,
        decimal? UnitPrice,
        decimal? IncludedQuota,
        DateTimeOffset? EffectiveFrom,
        DateTimeOffset? EffectiveTo);

    private static int CodePointLength(string value)
    {
        var length = 0;

        foreach (var _ in value.EnumerateRunes())
        {
            length++;
        }

        return length;
    }
}
