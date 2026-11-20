using System.Text.Json;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// Reads the parse child's XML record stream (start / text / end) as one record at a time.
/// <para>
/// The child already ran the untrusted bytes through the parser profile inside its resource limits, so what arrives
/// here is the profile's own output: names and values the profile accepted. A record that breaks the child's protocol
/// (a name that is not an XML name, a record that is not the expected shape) is a crashed child, never a verdict about
/// the report.
/// </para>
/// <para>
/// <b>Text values are never decoded.</b> No mapper reads element text (a status is an element name and the identity
/// and counters are attributes), and decoding a megabyte-sized text record would cost the Worker several times its
/// size in strings and buffers. A text record is checked for its shape and its value token skipped in place.
/// </para>
/// </summary>
internal static class ChildXmlRecords
{
    private const string KindProperty = "t";
    private const string XmlKind = "xml";
    private const string EventProperty = "e";
    private const string NameProperty = "n";
    private const string ValueProperty = "v";
    private const string AttributesProperty = "a";
    private const string StartEvent = "start";
    private const string EndEvent = "end";
    private const string TextEvent = "text";

    /// <summary>Twice the profile's element depth: slack for how the child counts, never room for a hostile stream.</summary>
    public const int MaxRebuiltDepth = ParsingLimits.MaxDepth * 2;

    /// <summary>Room over the report cap for the names and quoting a rebuilt document spelled differently from the source.</summary>
    public const long DocumentSlackBytes = 1024 * 1024;

    /// <summary>
    /// Decodes one record's framing and names. Throws for a record the child could not have written from a document
    /// the profile accepted; the caller turns that into <see cref="Crashed"/> so a fault never escapes the sink.
    /// </summary>
    /// <param name="utf8">The record's JSON object, without the terminating newline.</param>
    /// <returns>The record's kind, its name and its attribute pairs.</returns>
    /// <exception cref="JsonException">The record is not one JSON object of the expected shape.</exception>
    /// <exception cref="InvalidOperationException">The record names an event this protocol does not carry.</exception>
    public static XmlRecord Decode(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        string? kind = null;
        string? xmlEvent = null;
        string? name = null;
        var hasStringValue = false;
        var closed = false;
        List<(string Key, string Value)>? attributes = null;

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("the record is not a JSON object");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                closed = true;
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("the record is not one flat object");
            }

            var property = reader.GetString();
            reader.Read();

            switch (property)
            {
                case KindProperty:
                    kind = reader.GetString();
                    break;
                case EventProperty:
                    xmlEvent = reader.GetString();
                    break;
                case NameProperty:
                    name = reader.GetString();
                    break;
                case ValueProperty:
                    // The token is checked, never materialised: a text node can be as large as the report.
                    hasStringValue = reader.TokenType == JsonTokenType.String;
                    SkipValue(ref reader);
                    break;
                case AttributesProperty:
                    attributes = ReadAttributes(ref reader);
                    break;
                default:
                    SkipValue(ref reader);
                    break;
            }
        }

        if (!closed)
        {
            throw new JsonException("the record object is not closed");
        }

        if (!string.Equals(kind, XmlKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the child streamed a record that is not an XML record");
        }

        return new XmlRecord(EventOf(xmlEvent), name, hasStringValue, attributes);
    }

    /// <summary>The bytes the rebuild wrote for one start record: the tag name, its attribute pairs and the quoting.</summary>
    /// <param name="record">A start record whose names the caller already validated.</param>
    /// <returns>The UTF-8 length of the start tag the rebuild produced.</returns>
    public static long StartTagBytes(in XmlRecord record)
    {
        var bytes = 1L + Utf8Bytes(record.Name ?? string.Empty);

        if (record.Attributes is { } attributes)
        {
            foreach (var (key, value) in attributes)
            {
                // '<' key '="' escaped '"' — the space before the name and the quoting are the rebuild's own bytes.
                bytes += 1 + Utf8Bytes(key) + 2 + EscapedValueBytes(value) + 1;
            }
        }

        return bytes + 1;
    }

    /// <summary>The bytes the rebuild wrote for one end record; it spelled the name it was closing.</summary>
    /// <param name="openName">The qualified name of the element being closed.</param>
    /// <returns>The UTF-8 length of the end tag the rebuild produced.</returns>
    public static long EndTagBytes(string openName) => 2 + Utf8Bytes(openName) + 1;

    /// <summary>The refusal a record stream that is not a well-formed document description earns.</summary>
    /// <returns>The refusal the caller defers until the run's verdict is known.</returns>
    public static JUnitParseRefusedException Crashed() =>
        new(ParseRunRejection.Crashed, "the parse child's record stream is not a well-formed document description");

    private static XmlRecordEvent EventOf(string? xmlEvent) => xmlEvent switch
    {
        StartEvent => XmlRecordEvent.Start,
        EndEvent => XmlRecordEvent.End,
        TextEvent => XmlRecordEvent.Text,
        _ => throw new InvalidOperationException("the child streamed an unknown XML event"),
    };

    /// <summary>
    /// Steps past one property value without materialising it. A scalar is left where it is - the caller's next
    /// <c>Read</c> steps over it - while a container must be skipped whole, or its members are read as properties.
    /// </summary>
    /// <param name="reader">The reader, positioned on the value being dropped.</param>
    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
        {
            reader.Skip();
        }
    }

    private static List<(string Key, string Value)> ReadAttributes(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("the attribute list is not an array");
        }

        var attributes = new List<(string Key, string Value)>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var key = reader.GetString() ?? throw new JsonException("an attribute has no name");
            reader.Read();
            var value = reader.GetString() ?? throw new JsonException("an attribute has no value");
            reader.Read();

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("an attribute is not a name/value pair");
            }

            attributes.Add((key, value));
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("the attribute list is not closed");
        }

        return attributes;
    }

    private static long Utf8Bytes(string text)
    {
        var bytes = 0L;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character)
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                bytes += 4;
                index++;
            }
            else
            {
                bytes += character < 0x80 ? 1 : character < 0x800 ? 2 : 3;
            }
        }

        return bytes;
    }

    private static long EscapedValueBytes(string value)
    {
        var bytes = 0L;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            // Each escape is the reference the rebuild wrote, not the character it stands for.
            if (character == '&')
            {
                bytes += 5;
            }
            else if (character is '<' or '>')
            {
                bytes += 4;
            }
            else if (character == '"')
            {
                bytes += 6;
            }
            else if (character is '\t' or '\n' or '\r')
            {
                // A whitespace character reference is always one hex digit wide.
                bytes += 5;
            }
            else if (char.IsHighSurrogate(character)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                bytes += 4;
                index++;
            }
            else
            {
                bytes += character < 0x80 ? 1 : character < 0x800 ? 2 : 3;
            }
        }

        return bytes;
    }
}

/// <summary>The three events the child's XML protocol carries.</summary>
internal enum XmlRecordEvent
{
    /// <summary>An element start, with its name and attribute pairs.</summary>
    Start,

    /// <summary>An element end; its name is optional and never needed to close the tree.</summary>
    End,

    /// <summary>An element's text, whose value is never decoded.</summary>
    Text,
}

/// <summary>One decoded XML record: what it is, and the names the mapper needs.</summary>
/// <param name="Event">Which of the three protocol events the record carries.</param>
/// <param name="Name">The qualified name a start record carries, or an end record's optional own name.</param>
/// <param name="HasStringValue">True when a text record carried a string value; its content is not kept.</param>
/// <param name="Attributes">A start record's attribute pairs as the child wrote them, or <see langword="null"/>.</param>
internal readonly record struct XmlRecord(
    XmlRecordEvent Event,
    string? Name,
    bool HasStringValue,
    List<(string Key, string Value)>? Attributes);
