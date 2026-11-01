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
}
