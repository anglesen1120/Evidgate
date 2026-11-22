using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Contract.Tests;

/// <summary>
/// Turns selected subtrees of <c>contracts/openapi.yaml</c> into <see cref="JsonNode"/>s for <see cref="OpenApiSpec"/>.
/// YamlDotNet's event parser does the YAML; this class only refuses what a contract test must never
/// read silently: anchors, aliases, tags, block scalars, a duplicate key in one mapping (a last-wins
/// would let a spec edit drop a required member unnoticed), a <c>&lt;&lt;</c> merge key (a YAML 1.1 tool would merge it,
/// this reader would keep it as a member), NaN and infinity spellings (never needed by a spec), nesting deeper than
/// <see cref="MaxDepth"/>, and anything but one mapping or sequence document.
/// Every refusal is a <see cref="NotSupportedException"/> carrying the line.
/// </summary>
internal static class YamlSubset
{
    /// <summary>The deepest mapping/sequence nesting accepted; the recursive reader never gets near a stack limit.</summary>
    internal const int MaxDepth = 64;

    /// <summary>Parses one document whose root is a mapping or sequence; null for empty input.</summary>
    /// <exception cref="NotSupportedException">A refused construct, a syntax error, or more than one document.</exception>
    public static JsonNode? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var parser = new Parser(new StringReader(text));
            parser.Consume<StreamStart>();
            if (parser.TryConsume<StreamEnd>(out _))
            {
                return null;
            }

            parser.Consume<DocumentStart>();
            var rootStart = parser.Current!.Start;
            var root = ReadNode(parser, 0);
            parser.Consume<DocumentEnd>();
            if (!parser.TryConsume<StreamEnd>(out _))
            {
                throw Refuse(parser.Current!.Start, "a second document");
            }

            return root is JsonObject or JsonArray ? root : throw Refuse(rootStart, "a root that is not a mapping or sequence");
        }
        catch (YamlException exception)
        {
            throw new NotSupportedException($"line {exception.Start.Line}: {exception.Message}", exception);
        }
    }

    private static JsonNode? ReadNode(Parser parser, int depth)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return ReadScalar(scalar);
        }

        if (parser.TryConsume<MappingStart>(out var mappingStart))
        {
            RefuseDecorations(mappingStart.Anchor, mappingStart.Tag, mappingStart.Start);
            RefuseTooDeep(depth + 1, mappingStart.Start);
            return ReadMapping(parser, depth + 1);
        }

        if (parser.TryConsume<SequenceStart>(out var sequenceStart))
        {
            RefuseDecorations(sequenceStart.Anchor, sequenceStart.Tag, sequenceStart.Start);
            RefuseTooDeep(depth + 1, sequenceStart.Start);
            var array = new JsonArray();
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                array.Add(ReadNode(parser, depth + 1));
            }

            return array;
        }

        throw Refuse(parser.Current!.Start, parser.Current is AnchorAlias ? "an alias" : "this node");
    }

    private static JsonObject ReadMapping(Parser parser, int depth)
    {
        var mapping = new JsonObject();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            RefuseDecorations(key.Anchor, key.Tag, key.Start);
            RefuseBlockStyle(key);
            if (key.Style == ScalarStyle.Plain && key.Value == "<<")
            {
                throw Refuse(key.Start, "a merge key");
            }

            if (!mapping.TryAdd(key.Value, ReadNode(parser, depth)))
            {
                throw Refuse(key.Start, $"duplicate mapping key '{key.Value}'");
            }
        }

        return mapping;
    }

    private static JsonValue? ReadScalar(Scalar scalar)
    {
        RefuseDecorations(scalar.Anchor, scalar.Tag, scalar.Start);
        RefuseBlockStyle(scalar);

        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(scalar.Value);
        }

        var value = scalar.Value;
        if (IsNonFinite(value))
        {
            throw Refuse(scalar.Start, "a NaN or infinity scalar");
        }

        return value switch
        {
            "" or "null" or "~" => null,
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                => JsonValue.Create(integer),
            _ when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                => double.IsFinite(number)
                    ? JsonValue.Create(number)
                    : throw Refuse(scalar.Start, "a NaN or infinity scalar"),
            _ => JsonValue.Create(value),
        };
    }

    private static bool IsNonFinite(string value)
    {
        var unsigned = value.Length > 0 && value[0] is '+' or '-' ? value[1..] : value;
        return unsigned.Equals("nan", StringComparison.OrdinalIgnoreCase)
            || unsigned.Equals(".nan", StringComparison.OrdinalIgnoreCase)
            || unsigned.Equals("infinity", StringComparison.OrdinalIgnoreCase)
            || unsigned.Equals(".inf", StringComparison.OrdinalIgnoreCase);
    }

    private static void RefuseTooDeep(int depth, Mark at)
    {
        if (depth > MaxDepth)
        {
            throw Refuse(at, $"nesting deeper than {MaxDepth} levels");
        }
    }

    private static void RefuseBlockStyle(Scalar scalar)
    {
        if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
        {
            throw Refuse(scalar.Start, "a block scalar");
        }
    }

    private static void RefuseDecorations(AnchorName anchor, TagName tag, Mark at)
    {
        if (!anchor.IsEmpty)
        {
            throw Refuse(at, "an anchor");
        }

        if (!tag.IsEmpty)
        {
            throw Refuse(at, "a tag");
        }
    }

    private static NotSupportedException Refuse(Mark at, string what)
        => new($"line {at.Line}: {what} is outside what the contract reader accepts");
}
