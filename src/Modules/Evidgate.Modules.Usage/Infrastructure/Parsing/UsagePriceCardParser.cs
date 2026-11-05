using System.Text.Json;
using System.Text.Unicode;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Infrastructure.Parsing;

/// <summary>
/// Parses one rate-card body into a <see cref="UsagePriceCard"/> or the refusals it earned. The body is
/// bounded before anything reads it, the bytes must be valid UTF-8, and every object is read against a closed
/// member list, so an unexpected member is refused rather than ignored. Nothing here echoes a value: a refusal carries
/// the member's path and a stable reason only.
/// <para>
/// Duplicate members are permitted by the document parser on purpose — the walk below refuses them itself, which is the
/// only way to name the member that repeated. Money is parsed from the raw token by
/// <see cref="UsagePriceCardRules.TryParseAmount"/>; no binary floating-point type is involved at any point.
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

        var violations = new List<UsagePriceViolation>();
        if (FindDuplicate(root, string.Empty) is { } duplicate)
        {
            violations.Add(duplicate);
        }

        var (version, prices) = ReadRoot(root, violations);
        if (version is not { } cardVersion || prices is not { } priceArray)
        {
            return UsagePriceCardParseResult.Refused(violations);
        }

        var rows = ReadRows(priceArray, violations);

        return violations.Count > 0
            ? UsagePriceCardParseResult.Refused(violations)
            : new UsagePriceCardParseResult(new UsagePriceCard(cardVersion, rows), []);
    }

    private static (string? Version, JsonElement? Prices) ReadRoot(
        JsonElement root,
        List<UsagePriceViolation> violations)
    {
        string? version = null;
        JsonElement? prices = null;
        var versionSeen = false;
        var pricesSeen = false;

        foreach (var member in root.EnumerateObject())
        {
            switch (member.Name)
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
                    violations.Add(new UsagePriceViolation("/" + member.Name, UsagePriceRefusalReasons.UnknownField));
                    break;
            }
        }

        if (!versionSeen)
        {
            violations.Add(new UsagePriceViolation("/" + VersionMember, UsagePriceRefusalReasons.Required));
        }

        if (!pricesSeen)
        {
            violations.Add(new UsagePriceViolation("/" + PricesMember, UsagePriceRefusalReasons.Required));
        }

        return (version, prices);
    }

    private static string? ReadVersion(JsonElement value, List<UsagePriceViolation> violations)
    {
        const string path = "/" + VersionMember;

        if (value.ValueKind != JsonValueKind.String)
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.InvalidValue));
            return null;
        }

        var text = value.GetString();
        if (text is null)
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.InvalidValue));
            return null;
        }

        if (text.Length == 0)
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.Required));
            return null;
        }

        if (CodePointLength(text) > UsagePriceCardRules.MaxVersionLength)
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.TooLong));
            return null;
        }

        if (!PostgresText.IsStorable(text))
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.InvalidValue));
            return null;
        }

        return text;
    }

    private static List<UsagePriceCardRow> ReadRows(JsonElement array, List<UsagePriceViolation> violations)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            violations.Add(new UsagePriceViolation("/" + PricesMember, UsagePriceRefusalReasons.InvalidValue));
            return [];
        }

        if (array.GetArrayLength() == 0)
        {
            violations.Add(new UsagePriceViolation("/" + PricesMember, UsagePriceRefusalReasons.Empty));
            return [];
        }

        if (array.GetArrayLength() > UsagePriceCardRules.MaxRows)
        {
            violations.Add(new UsagePriceViolation("/" + PricesMember, UsagePriceRefusalReasons.TooMany));
            return [];
        }

        var before = violations.Count;
        var rows = new List<UsagePriceCardRow>(array.GetArrayLength());
        var index = 0;

        foreach (var element in array.EnumerateArray())
        {
            if (ReadRow(element, "/" + PricesMember + "/" + index, violations) is { } row)
            {
                rows.Add(row);
            }

            index++;
        }

        // The cross-row rules need every row parsed; a row that already failed reports its own reason instead.
        if (violations.Count == before)
        {
            violations.AddRange(UsagePriceCardRules.ValidateRows(rows));
        }

        return rows;
    }

    private static UsagePriceCardRow? ReadRow(
        JsonElement element,
        string path,
        List<UsagePriceViolation> violations)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.InvalidValue));
            return null;
        }

        if (FindDuplicate(element, path) is { } duplicate)
        {
            violations.Add(duplicate);
        }

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
        List<UsagePriceViolation> violations)
    {
        var values = new RowValues(null, null, null, null, null, null);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in element.EnumerateObject())
        {
            seen.Add(member.Name);

            switch (member.Name)
            {
                case MetricMember:
                    values = values with { MetricName = ReadText(member.Value, path + "/" + MetricMember, violations) };
                    break;
                case UnitPriceMember:
                    values = values with { UnitPrice = ReadAmount(member.Value, path + "/" + UnitPriceMember, violations) };
                    break;
                case CurrencyMember:
                    values = values with { Currency = ReadText(member.Value, path + "/" + CurrencyMember, violations) };
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
                    violations.Add(new UsagePriceViolation(
                        path + "/" + member.Name, UsagePriceRefusalReasons.UnknownField));
                    break;
            }
        }

        return (values, seen);
    }

    private static UsageMetric? ValidateRow(
        RowValues values,
        string path,
        List<UsagePriceViolation> violations)
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
                violations.Add(new UsagePriceViolation(
                    path + "/" + MetricMember, UsagePriceRefusalReasons.UnknownMetric));
            }
        }

        if (values.Currency is not null && !UsagePriceCardRules.IsCurrency(values.Currency))
        {
            violations.Add(new UsagePriceViolation(
                path + "/" + CurrencyMember, UsagePriceRefusalReasons.InvalidValue));
        }

        if (values.EffectiveFrom is { } from && values.EffectiveTo is { } to && to <= from)
        {
            violations.Add(new UsagePriceViolation(
                path + "/" + EffectiveToMember, UsagePriceRefusalReasons.InvalidValue));
        }

        return metric;
    }

    private static string? ReadText(JsonElement value, string path, List<UsagePriceViolation> violations)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.InvalidValue));
            return null;
        }

        return value.GetString();
    }

    private static decimal? ReadAmount(JsonElement value, string path, List<UsagePriceViolation> violations)
    {
        // A JSON number is read from its raw token: converting through a floating-point type would round the money.
        var token = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };

        if (token is null || !UsagePriceCardRules.TryParseAmount(token, out var amount))
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.InvalidAmount));
            return null;
        }

        return amount;
    }

    private static decimal? ReadNullableAmount(JsonElement value, string path, List<UsagePriceViolation> violations) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadAmount(value, path, violations);

    private static DateTimeOffset? ReadInstant(JsonElement value, string path, List<UsagePriceViolation> violations)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.InvalidValue));
            return null;
        }

        var text = value.GetString();
        if (text is null || !UsagePriceCardRules.TryParseInstant(text, out var instant))
        {
            violations.Add(new UsagePriceViolation(path, UsagePriceRefusalReasons.InvalidValue));
            return null;
        }

        return instant;
    }

    private static DateTimeOffset? ReadNullableInstant(
        JsonElement value,
        string path,
        List<UsagePriceViolation> violations) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadInstant(value, path, violations);

    private static void RequireSeen(
        HashSet<string> seen,
        string member,
        string path,
        List<UsagePriceViolation> violations)
    {
        if (!seen.Contains(member))
        {
            violations.Add(new UsagePriceViolation(path + "/" + member, UsagePriceRefusalReasons.Required));
        }
    }

    private static UsagePriceViolation? FindDuplicate(JsonElement element, string path)
    {
        HashSet<string>? names = null;

        foreach (var member in element.EnumerateObject())
        {
            names ??= new HashSet<string>(StringComparer.Ordinal);

            if (!names.Add(member.Name))
            {
                return new UsagePriceViolation(path + "/" + member.Name, UsagePriceRefusalReasons.DuplicateMember);
            }
        }

        return null;
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
