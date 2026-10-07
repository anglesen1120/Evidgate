using System.Text.Json;

namespace Evidgate.Modules.Ingestion.Domain;

/// <summary>
/// The unstorable-text guard of 04.7.1-T2.2/T2.3, shared by the capability body (Application) and the
/// first-send extractor (Domain) so Domain never reaches into Application (00 §5.5). True when a member
/// name or string anywhere in the element is no text PostgreSQL and the RFC 8785 form can carry: an
/// unpaired UTF-16 surrogate (which also makes the schema engine throw) or U+0000 (refused by jsonb).
/// Checked before the schema runs, so such a body is a 422, never a 400 or a 500.
/// </summary>
internal static class UnstorableText
{
    /// <summary>True when <paramref name="element"/> carries a member name or string that cannot be stored.</summary>
    /// <param name="element">The parsed document root or capability object.</param>
    internal static bool Contains(JsonElement element)
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
}
