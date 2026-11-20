using System.Text.Json;

namespace Evidgate.BuildingBlocks.SharedKernel.Serialization;

/// <summary>
/// The one detector for text PostgreSQL jsonb and the RFC 8785 form cannot carry. The walk is recursive over a
/// parsed <see cref="JsonElement"/>, whose depth the parser already capped (64 by default), so it cannot overflow
/// the stack; its cost is linear in the body, which the route's body limit bounds.
/// </summary>
public static class UnstorableJsonText
{
    /// <summary>
    /// True when a member name or string anywhere in <paramref name="element"/> is no text PostgreSQL and the RFC 8785 form can
    /// carry: an unpaired UTF-16 surrogate (<c>"\ud800"</c>, which also makes the schema engine throw) or U+0000 (refused by
    /// jsonb). Callers check it before the schema runs so such a body is refused, never a 500.
    /// </summary>
    public static bool HasUnstorableText(JsonElement element) => Walk(element, nulIsUnstorable: true);

    /// <summary>
    /// True when a member name or string anywhere in <paramref name="element"/> holds an unpaired UTF-16 surrogate. U+0000
    /// does not count: the writer emits it as the <c>\u0000</c> escape, so a probe that classifies write failures must not
    /// treat it as the cause of one.
    /// </summary>
    public static bool HasUnpairedSurrogate(JsonElement element) => Walk(element, nulIsUnstorable: false);

    /// <summary>
    /// True when a member name at any depth of <paramref name="element"/> is unstorable (a surrogate without its partner, or
    /// U+0000); string values are not looked at. A name that cannot be transcoded counts. Use it to tell an unreadable name,
    /// which no code may read, from unreadable text in a value, which can be read around.
    /// </summary>
    public static bool HasUnstorableName(JsonElement element) => Walk(element, nulIsUnstorable: true, namesOnly: true);

    /// <summary>NUL, or a surrogate without its partner.</summary>
    public static bool Unstorable(string text) => Has(text, nulIsUnstorable: true);

    private static bool Walk(JsonElement element, bool nulIsUnstorable, bool namesOnly = false)
    {
        try
        {
            return Visit(element, nulIsUnstorable, namesOnly);
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json refuses to transcode an invalid escape sequence.
            return true;
        }

        static bool Visit(JsonElement element, bool nulIsUnstorable, bool namesOnly) => element.ValueKind switch
        {
            JsonValueKind.String => !namesOnly && Has(element.GetString()!, nulIsUnstorable),
            JsonValueKind.Array => element.EnumerateArray().Any(item => Visit(item, nulIsUnstorable, namesOnly)),
            JsonValueKind.Object => element.EnumerateObject()
                .Any(member => Has(member.Name, nulIsUnstorable) || Visit(member.Value, nulIsUnstorable, namesOnly)),
            _ => false,
        };
    }

    private static bool Has(string text, bool nulIsUnstorable)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (nulIsUnstorable && c == '\0')
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
}
