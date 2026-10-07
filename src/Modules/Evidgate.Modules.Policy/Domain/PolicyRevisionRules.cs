using System.Text.Json;
using System.Text.RegularExpressions;

namespace Evidgate.Modules.Policy.Domain;

/// <summary>
/// Shape rules of a policy revision (07.1.1-T1): the content address is <c>sha256:&lt;64 hex&gt;</c> (00 §7) and the
/// body is a JSON object (00 §8.6) carrying only text jsonb can store. Both are checked before the store touches the
/// database, so a malformed append never reaches a statement.
/// </summary>
internal static partial class PolicyRevisionRules
{
    /// <summary>True when <paramref name="hash"/> is a <c>sha256:&lt;64 lower-case hex&gt;</c> content address.</summary>
    public static bool IsValidHash(string? hash) => hash is not null && HashShape().IsMatch(hash);

    /// <summary>
    /// True when <paramref name="body"/> parses as a JSON object whose text jsonb can store: no U+0000 and no unpaired
    /// UTF-16 surrogate in a member name or string value (the 05.1.1/05.1.2 <c>JsonBody</c> rule).
    /// </summary>
    public static bool IsJsonObject(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && !HasUnstorableText(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a member name or string anywhere in <paramref name="element"/> is no text PostgreSQL can carry: an
    /// unpaired UTF-16 surrogate (<c>"\ud800"</c>) or U+0000 (refused by jsonb). The 05.1.1/05.1.2 <c>JsonBody</c>
    /// walk, kept local because that helper is internal to the Orchestrator module.
    /// </summary>
    private static bool HasUnstorableText(JsonElement element)
    {
        try
        {
            return Walk(element);
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json refuses to transcode an invalid escape sequence.
            return true;
        }

        static bool Walk(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => Unstorable(element.GetString()!),
            JsonValueKind.Array => element.EnumerateArray().Any(Walk),
            JsonValueKind.Object => element.EnumerateObject().Any(member => Unstorable(member.Name) || Walk(member.Value)),
            _ => false,
        };
    }

    /// <summary>NUL, or a surrogate without its partner.</summary>
    private static bool Unstorable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
            {
                return true;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    // \z, not $: $ also matches before a trailing "\n", which the Postgres CHECK refuses (07.1.1-T1 review C4).
    [GeneratedRegex("^sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex HashShape();
}
