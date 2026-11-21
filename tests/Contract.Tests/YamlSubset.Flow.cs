using System.Text.Json.Nodes;

namespace Contract.Tests;

// Flow mappings live here so YamlSubset.cs stays under the file-size limit.
internal sealed partial class YamlSubset
{
    /// <summary>
    /// Parses a flow mapping (<c>{ $ref: '#/components/schemas/X' }</c>, the form the spec's page schemas use for
    /// their item reference). Each entry is read with the block-mapping syntax, so a nested flow collection works.
    /// </summary>
    private JsonObject ParseFlowMapping(string value, int lineNumber)
    {
        var close = ClosingDelimiter(value, '{', '}', lineNumber);
        if (StripComment(value[(close + 1)..], lineNumber).Trim().Length != 0)
        {
            throw new NotSupportedException($"line {lineNumber}: trailing text after a flow mapping: '{value}'");
        }

        var mapping = new JsonObject();
        var inner = value[1..close].Trim();
        if (inner.Length == 0)
        {
            return mapping;
        }

        foreach (var part in SplitTopLevel(inner, lineNumber))
        {
            var (key, rest) = SplitEntry(part.Trim(), lineNumber);
            if (rest.Length == 0)
            {
                throw new NotSupportedException($"line {lineNumber}: flow mapping entry without a value: '{part.Trim()}'");
            }

            AddMember(mapping, key, ParseScalar(rest, lineNumber), lineNumber);
        }

        return mapping;
    }

    /// <summary>
    /// True when the quote at <paramref name="i"/> opens a quoted scalar. YAML only opens one at the start of a
    /// value or flow item; an apostrophe inside a plain scalar (<c>don't</c>) is literal text.
    /// </summary>
    private static bool IsQuoteStart(string text, int i)
    {
        if (text[i] is not ('\'' or '"'))
        {
            return false;
        }

        var before = i - 1;
        while (before >= 0 && text[before] == ' ')
        {
            before--;
        }

        return before < 0 || text[before] is '[' or '{' or ',' or ':';
    }

    /// <summary>
    /// Joins the following lines onto a flow collection that does not close on its first line (Prettier wraps long
    /// flow sequences). Comments are dropped per line; a collection still open at end of input is refused.
    /// </summary>
    private string JoinFlowLines(string text, int lineNumber)
    {
        var value = StripComment(text, lineNumber).Trim();
        if (value.Length == 0 || value[0] is not ('[' or '{'))
        {
            return text;
        }

        while (FlowDepth(value, lineNumber) > 0)
        {
            if (_index >= _lines.Count)
            {
                throw new NotSupportedException($"line {lineNumber}: flow collection is never closed: '{value}'");
            }

            value += " " + StripComment(_lines[_index++], _index).Trim();
        }

        return value;
    }

    /// <summary>Open-bracket depth of <paramref name="value"/> after its last character, skipping quoted text.</summary>
    private static int FlowDepth(string value, int lineNumber)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (IsQuoteStart(value, i))
            {
                i = SkipQuoted(value, i, lineNumber);
            }
            else if (value[i] is '[' or '{')
            {
                depth++;
            }
            else if (value[i] is ']' or '}')
            {
                depth--;
            }
        }

        return depth;
    }
}
