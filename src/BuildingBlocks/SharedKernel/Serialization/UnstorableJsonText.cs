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
    public static bool HasUnstorableText(JsonElement element)
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
    public static bool Unstorable(string text)
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
}
