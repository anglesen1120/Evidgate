using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Contract.Tests;

/// <summary>
/// Turns selected subtrees of <c>contracts/openapi.yaml</c> into <see cref="JsonNode"/>s for <see cref="OpenApiSpec"/>.
/// YamlDotNet's event parser does the YAML; this class only refuses what a contract test must never
/// read silently: anchors, aliases, tags, block scalars, a duplicate key in one mapping (a last-wins
/// would let a spec edit drop a required member unnoticed), and anything but one mapping or sequence document.
/// Every refusal is a <see cref="NotSupportedException"/> carrying the line.
/// </summary>
internal static class YamlSubset
{
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
            var root = ReadNode(parser);
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

    private static JsonNode? ReadNode(Parser parser)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return ReadScalar(scalar);
        }

        if (parser.TryConsume<MappingStart>(out var mappingStart))
        {
            RefuseDecorations(mappingStart.Anchor, mappingStart.Tag, mappingStart.Start);
            return ReadMapping(parser);
        }

        if (parser.TryConsume<SequenceStart>(out var sequenceStart))
        {
            RefuseDecorations(sequenceStart.Anchor, sequenceStart.Tag, sequenceStart.Start);
            var array = new JsonArray();
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                array.Add(ReadNode(parser));
            }

            return array;
        }

        throw Refuse(parser.Current!.Start, parser.Current is AnchorAlias ? "an alias" : "this node");
    }

    private static JsonObject ReadMapping(Parser parser)
    {
        var mapping = new JsonObject();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            RefuseDecorations(key.Anchor, key.Tag, key.Start);
            if (!mapping.TryAdd(key.Value, ReadNode(parser)))
            {
                throw Refuse(key.Start, $"duplicate mapping key '{key.Value}'");
            }
        }

        return mapping;
    }

    private static JsonValue? ReadScalar(Scalar scalar)
    {
        RefuseDecorations(scalar.Anchor, scalar.Tag, scalar.Start);
        if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
        {
            throw Refuse(scalar.Start, "a block scalar");
        }

        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(scalar.Value);
        }

        var value = scalar.Value;
        return value switch
        {
            "" or "null" or "~" => null,
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                => JsonValue.Create(integer),
            _ when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                => JsonValue.Create(number),
            _ => JsonValue.Create(value),
        };
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
