using System.Text.RegularExpressions;
using Corvus.Json;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace Evidgate.BuildingBlocks.Contracts.Validation;

/// <summary>
/// Maps one Corvus <see cref="ValidationResult"/> to the shared
/// <see cref="ContractValidationError"/> row of the parity corpus (01.3.2-T2,
/// fixtures/contracts/README.md). The CLI module <c>packages/cli/src/validate.ts</c>
/// implements the identical mapping on Ajv's error list; both suites assert the
/// same (index, path, reason) sets.
/// </summary>
/// <remarks>
/// Normalisation rules, identical on both runtimes:
/// - Composite keywords are dropped — Corvus wraps leaf failures in
///   <c>allOf</c>/<c>then</c> wrapper results (and Ajv emits an <c>if</c> row for a
///   failed <c>then</c>); keeping only the leaf keeps the sets equal.
/// - <c>required</c> (and Corvus's <c>properties</c>-keyword report of a missing
///   member) and <c>additionalProperties</c> put the offending member name at the
///   end of the path, so <c>/events/3/status</c> names the absent field directly.
/// - A trailing array index on a keyword that names the array itself
///   (<c>uniqueItems</c>) is stripped — Corvus points at the duplicate element,
///   Ajv at the array.
/// - The engine's diagnostic text is reduced to the schema keyword in lower_snake
///   (Ajv's keyword names verbatim) so the reason is identical in both runtimes.
/// </remarks>
internal static partial class ContractErrorMapper
{

    /// <summary>
    /// Composite keywords that carry their own failure row in both engines —
    /// self-leaf diagnostics that survive even when the row has child results
    /// (a <c>not</c> success emits its subschema's failure rows, which are
    /// diagnostics of a satisfied negation, never evidence). The row location is
    /// the composite schema node (Corvus locates the <c>not</c> failure at the
    /// parent pointer, one segment above the keyword).
    /// </summary>
    private static readonly HashSet<string> ReportingKeywords = new(StringComparer.Ordinal)
    {
        "anyOf",
        "oneOf",
        "not",
        "contains",
    };


    /// <summary>
    /// Composite keywords that exist only to wrap a child result: both engines
    /// drop them when a child row carries the failure (<c>allOf</c> wrapping a
    /// <c>required</c>, <c>then</c>/<c>else</c> carrying the Ajv <c>if</c> row's
    /// evidence). A self-leaf wrapper failure — a composite row with no
    /// qualifying child — still surfaces, so a real violation can never vanish.
    /// </summary>
    private static readonly HashSet<string> WrapperKeywords = new(StringComparer.Ordinal)
    {
        "allOf",
        "if",
        "then",
        "else",
        "dependentSchemas",
        "properties",
        "propertyNames",
        "items",
        "additionalItems",
    };

    /// <summary>
    /// Every ancestor keyword location of every invalid result — the wrapper
    /// rows whose failure a child already carries.
    /// </summary>
    public static HashSet<string> CoveredKeywordLocations(ValidationContext context)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in context.Results)
        {
            if (result.Valid || result.Location is not { } location)
            {
                continue;
            }

            // The bare-URI root row (fragment "") is covered whenever any result
            // at all exists — a root-level wrapper is never a leaf.
            if (covered.Count == 0)
            {
                covered.Add(string.Empty);
            }

            var fragment = Fragment(location.Item2.ToString());
            while (fragment.LastIndexOf('/') is { } marker && marker >= 0)
            {
                fragment = fragment[..marker];
                covered.Add(fragment);
                if (marker == 0)
                {
                    break;
                }
            }
        }

        return covered;
    }

    /// <summary>
    /// Maps one Corvus <see cref="ValidationResult"/> to the shared row shape —
    /// the counterpart of <c>mapError</c> in <c>packages/cli/src/validate.ts</c>.
    /// <paramref name="coveredKeywordLocations"/> is
    /// <see cref="CoveredKeywordLocations"/>: the ancestors of every invalid
    /// result's keyword location, used to tell a wrapper row (dropped — the
    /// child carries the failure) from a self-leaf composite failure (kept, so
    /// a real violation never produces an empty mapped list).
    /// </summary>
    public static ContractValidationError? Map(
        ValidationResult result,
        JsonNode? schemaDocument,
        int? index,
        string prefix,
        IReadOnlySet<string> coveredKeywordLocations)
    {
        var keywordFragment = string.Empty;
        var instanceFragment = string.Empty;
        if (result.Location is { } location)
        {
            keywordFragment = Fragment(location.Item2.ToString());
            instanceFragment = Fragment(location.Item3.ToString());
        }

        // Skip the empty leading segment a '/'-led fragment produces.
        var segments = keywordFragment.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var lastSegment = segments.Length > 0 ? segments[^1] : null;

        var message = result.Message ?? string.Empty;
        var messageKeyword = KeywordFromMessage(message);

        // Diagnostics of a satisfied composite, never evidence:
        // - 'if' rows anywhere — the conditional's own keyword is dropped in
        //   both runtimes (Ajv's identical rule is error.keyword === 'if').
        // - rows inside a 'not' subtree — a satisfied negation emits its
        //   subschema's failure rows; only the 'not' row itself is real.
        // - rows inside a reporting composite's subtree ('anyOf'/'oneOf'
        //   branch diagnostics, 'contains' item checks) — the self row carries
        //   the failure, matching the rows Ajv reports for these keywords.
        // Known limitation, inherited from the segment-level check: a property
        // literally named 'not'/'if' (or anyOf/oneOf/contains/… mid-path) is
        // indistinguishable from the keyword diagnostic and also drops — the
        // mapped set then differs from Ajv's.
        for (var i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            var segment = segments[i];
            if (segment is "if" or "not"
                || (!isLast && segment is "anyOf" or "oneOf" or "contains" or "propertyNames"))
            {
                return null;
            }
        }

        string? keyword;
        string? member = null;
        if (ReportingKeywords.Contains(messageKeyword ?? string.Empty))
        {
            // A reporting keyword's own row: prefer the fragment's terminal
            // segment when it names the keyword (contains → '#/contains'…);
            // the root-level 'not' row carries no fragment, so its keyword
            // comes from the message.
            keyword = lastSegment is { } tail && ReportingKeywords.Contains(tail)
                ? tail
                : messageKeyword;
        }
        else if (RequiredProperty.Match(message) is { Success: true } requiredMatch)
        {
            keyword = "required";
            member = requiredMatch.Groups[1].Value;
        }
        else
        {
            keyword = messageKeyword;
            if (keyword is null && segments.Length > 0 && keywordFragment.Length > 0)
            {
                // Empty-message rows carry the keyword in their location, e.g.
                // #/additionalProperties/foo — and the member is already the
                // instance pointer's last segment, so nothing is appended
                // (unlike 'required').
                keyword = Unescape(lastSegment!);
                var previous = segments.Length > 1 ? segments[^2] : null;
                if (previous == "additionalProperties")
                {
                    keyword = "additionalProperties";
                }
            }

            if (keyword == "properties")
            {
                // Corvus reports a missing required member under the 'properties'
                // keyword; map to 'required' + the member, matching Ajv.
                keyword = "required";
                member = ResolveRequiredMember(schemaDocument, keywordFragment);
                if (member is null && RequiredProperty.Match(message) is { Success: true } retry)
                {
                    member = retry.Groups[1].Value;
                }
            }

            if (keyword == "required" && member is null)
            {
                member = ResolveRequiredMember(schemaDocument, keywordFragment);
            }

            if (keyword is not null && WrapperKeywords.Contains(keyword)
                && coveredKeywordLocations.Contains(keywordFragment))
            {
                // A genuine wrapper row — the child result carries the failure.
                return null;
            }
        }
        var instancePath = instanceFragment;

        // 'dependentSchemas' applies to the object that owns the triggering
        // member; Corvus reports the instance at the triggering member itself
        // (one level deeper than Ajv, which names the object) — strip it.
        var dependentIndex = Array.IndexOf(segments, "dependentSchemas");
        if (dependentIndex >= 0 && dependentIndex + 1 < segments.Length
            && instancePath.Length > 0)
        {
            var trigger = "/" + Escape(Unescape(segments[dependentIndex + 1]));
            if (instancePath == trigger)
            {
                instancePath = string.Empty;
            }
            else if (instancePath.StartsWith(trigger + "/", StringComparison.Ordinal))
            {
                instancePath = instancePath[trigger.Length..];
            }
        }

        if (keyword == "uniqueItems" && instancePath.Length > 0
            && instancePath[(instancePath.LastIndexOf('/') + 1)..] is { } tailPath
            && int.TryParse(tailPath, out _))
        {
            // Corvus locates uniqueItems at the duplicate element; Ajv at the array.
            instancePath = instancePath[..instancePath.LastIndexOf('/')];
        }

        var path = prefix + instancePath + (member is null ? string.Empty : "/" + Escape(member));
        return new ContractValidationError(index, path, ToSnake(keyword ?? "invalid"));
    }

    /// <summary>The fragment after the last '#' in a schema or instance location.</summary>
    private static string Fragment(string reference)
    {
        var marker = reference.LastIndexOf('#');
        return marker < 0 ? string.Empty : reference[(marker + 1)..];
    }

    /// <summary><c>Validation &lt;keyword&gt; - …</c> or <c>Validation - &lt;what&gt; …</c>.</summary>
    private static string? KeywordFromMessage(string message)
    {
        if (!message.StartsWith("Validation", StringComparison.Ordinal))
        {
            return null;
        }

        var match = MessageKeyword.Match(message);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Resolves the schema-relative pointer (e.g. <c>#/required/5</c>) against the
    /// document that produced the result to recover the missing member name.
    /// </summary>
    private static string? ResolveRequiredMember(JsonNode? schemaDocument, string pointer)
    {
        if (schemaDocument is null || pointer.Length == 0)
        {
            return null;
        }

        var current = schemaDocument;
        foreach (var rawSegment in pointer.Split('/'))
        {
            if (rawSegment.Length == 0)
            {
                continue;
            }

            var segment = Unescape(rawSegment);
            switch (current)
            {
                case JsonObject jsonObject when jsonObject.TryGetPropertyValue(segment, out var child):
                    current = child;
                    break;
                case JsonArray jsonArray when int.TryParse(segment, out var i) && i >= 0 && i < jsonArray.Count:
                    current = jsonArray[i];
                    break;
                default:
                    return null;
            }

            if (current is null)
            {
                return null;
            }
        }

        return current is JsonValue value && value.TryGetValue<string>(out var member) ? member : null;
    }

    /// <summary>JSON pointer escapes: <c>~0</c> for <c>~</c>, <c>~1</c> for <c>/</c>.</summary>
    private static string Unescape(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    /// <inheritdoc cref="Unescape" />
    private static string Escape(string member) => member.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>camelCase keyword → lower_snake reason token.</summary>
    private static string ToSnake(string keyword) =>
        CamelBoundary.Replace(keyword, "$1_$2").ToLowerInvariant();

    [GeneratedRegex(@"required property '([^']+)' was not present", RegexOptions.None)]
    private static partial Regex RequiredProperty { get; }

    [GeneratedRegex(@"^Validation\s+(?:-\s+)?([A-Za-z]+)", RegexOptions.None)]
    private static partial Regex MessageKeyword { get; }

    [GeneratedRegex(@"([a-z0-9])([A-Z])", RegexOptions.None)]
    private static partial Regex CamelBoundary { get; }
}
