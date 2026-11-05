using System.Text;
using System.Text.Json;
using System.Xml;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// Turns the parse child's XML record stream (start / text / end records) back into a document a mapper can walk.
/// <para>
/// The child already ran the untrusted bytes through the parser profile inside its resource limits, so what arrives here
/// is the profile's own output: names and values the profile accepted, no document type, a balanced element tree.
/// The rebuilt text is therefore read back with a reader that prohibits a document type and resolves nothing; the
/// parent never hands the report's own bytes to an XML reader.
/// <para>
/// <b>Text nodes are not rebuilt.</b> No mapper reads element text (a status is an element name and the identity and
/// counters are attributes), and decoding a megabyte-sized text record would cost the Worker several times its size in
/// strings and buffers. Text records are checked to be well-formed records and skipped without decoding their value,
/// so the rebuilt document is bounded by the structure and attributes the profile already admitted.
/// </para>
/// <para>
/// A record that breaks the child's protocol (an unbalanced
/// tree, a name that is not an XML name, a record that is not the expected shape) is a crashed child, never a verdict
/// about the report.
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
    private const int MaxRebuiltDepth = ParsingLimits.MaxDepth * 2;

    /// <summary>
    /// Rebuilds the document the records describe and opens a hardened reader over it.
    /// </summary>
    /// <param name="records">The child's records in stream order, without its marker.</param>
    /// <param name="maxDocumentBytes">The most the rebuilt document may take; a stream that needs more is a crashed child.</param>
    /// <returns>A reader over the rebuilt document; the caller disposes it.</returns>
    /// <exception cref="JUnitParseRefusedException">The stream is not a well-framed element tree (<c>parse_crashed</c>).</exception>
    public static XmlReader OpenReader(IReadOnlyList<ParseRecord> records, long maxDocumentBytes)
    {
        ArgumentNullException.ThrowIfNull(records);

        try
        {
            var buffer = new MemoryStream();
            using (var text = new StreamWriter(buffer, new UTF8Encoding(false), 4096, leaveOpen: true))
            {
                Rebuild(records, text, buffer, maxDocumentBytes);
            }

            buffer.Position = 0;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                CloseInput = true,
            };

            return XmlReader.Create(buffer, settings);
        }
        catch (Exception exception) when (exception is JsonException or XmlException or InvalidOperationException or IOException)
        {
            throw Crashed();
        }
    }

    private static void Rebuild(
        IReadOnlyList<ParseRecord> records,
        StreamWriter output,
        MemoryStream buffer,
        long maxDocumentBytes)
    {
        var open = new Stack<string>();
        var roots = 0;

        foreach (var record in records)
        {
            if (buffer.Length > maxDocumentBytes)
            {
                throw new InvalidOperationException("the child's records rebuild a document over the report's byte budget");
            }

            var parsed = Decode(record.Utf8Json.Span);
            if (parsed.Kind is not XmlKind)
            {
                throw new InvalidOperationException("the child streamed a record that is not an XML record");
            }

            switch (parsed.Event)
            {
                case StartEvent:
                    if (open.Count == 0 && ++roots > 1)
                    {
                        throw new InvalidOperationException("the child streamed more than one root element");
                    }

                    if (open.Count >= MaxRebuiltDepth)
                    {
                        throw new InvalidOperationException("the child streamed elements nested deeper than the profile admits");
                    }

                    WriteStart(output, parsed);
                    open.Push(parsed.Name!);
                    break;

                case EndEvent:
                    if (!open.TryPop(out var name))
                    {
                        throw new InvalidOperationException("the child streamed an end record with no open element");
                    }

                    output.Write("</");
                    output.Write(name);
                    output.Write('>');
                    break;

                case TextEvent:
                    // Skipped without being decoded (see the type remarks). Text outside the root is only ever the
                    // document-level whitespace the profile's reader reports, which carries nothing either.
                    break;

                default:
                    throw new InvalidOperationException("the child streamed an unknown XML event");
            }
        }

        if (open.Count != 0 || roots != 1)
        {
            throw new InvalidOperationException("the child's element records do not form one balanced document");
        }
    }

    private static void WriteStart(StreamWriter output, DecodedRecord record)
    {
        output.Write('<');
        output.Write(XmlConvert.VerifyName(record.Name ?? throw new InvalidOperationException("a start record has no name")));

        foreach (var (key, value) in record.Attributes)
        {
            output.Write(' ');
            output.Write(XmlConvert.VerifyName(key));
            output.Write("=\"");
            WriteEscaped(output, value);
            output.Write('"');
        }

        output.Write('>');
    }

    private static void WriteEscaped(StreamWriter output, string value)
    {
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    output.Write("&amp;");
                    break;
                case '<':
                    output.Write("&lt;");
                    break;
                case '>':
                    output.Write("&gt;");
                    break;
                case '"':
                    output.Write("&quot;");
                    break;
                case '\t':
                case '\n':
                case '\r':
                    // Character references keep whitespace the reader would otherwise normalise inside an attribute.
                    output.Write("&#x");
                    output.Write(((int)character).ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                    output.Write(';');
                    break;
                default:
                    output.Write(character);
                    break;
            }
        }
    }

    private static DecodedRecord Decode(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        string? kind = null;
        string? xmlEvent = null;
        string? name = null;
        var attributes = new List<(string Key, string Value)>();

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("the record is not a JSON object");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
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
                    // The only record with a value is a text record, whose value is never decoded.
                    reader.Skip();
                    break;
                case AttributesProperty:
                    ReadAttributes(ref reader, attributes);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new DecodedRecord(kind, xmlEvent, name, attributes);
    }

    private static void ReadAttributes(ref Utf8JsonReader reader, List<(string Key, string Value)> attributes)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("the attribute list is not an array");
        }

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
    }

    public static JUnitParseRefusedException Crashed() =>
        new(ParseRunRejection.Crashed, "the parse child's record stream is not a well-formed document description");

    private sealed record DecodedRecord(
        string? Kind,
        string? Event,
        string? Name,
        List<(string Key, string Value)> Attributes);
}
