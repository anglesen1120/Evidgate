using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Parser;

/// <summary>
/// The child's stdout protocol (06.3.6 §7): UTF-8, one JSON object per line with <c>\n</c> line ends, and a
/// final <c>{"done":true,"count":n}</c> marker written once, last, after every record.
/// <para>
/// Every record is built with <see cref="Utf8JsonWriter"/> over the strict <see cref="JavaScriptEncoder.Default"/>,
/// so a quote, a backslash, a control character, a line separator (U+2028/U+2029) or any non-ASCII character in
/// an artifact — an element name, an attribute value, a text node, a zip entry name — leaves as a JSON escape. No
/// artifact byte can therefore end a line, forge a record or imitate the marker: the framing is the writer's, not
/// the input's. The count is the number of records before the marker — a <c>rejected</c> record is counted like
/// any other, so empty stdin emits one rejection and a marker with <c>count</c> 1 — so a consumer that finds the
/// marker missing, repeated, not last, or counting a different number of lines treats the run as a crash
/// (06.3.6 AC3).
/// </para>
/// <para>
/// A record is buffered in full before one byte reaches the stream (see <see cref="WriteBuffer"/>), so a record
/// the writer cannot represent is dropped without leaving a partial line behind: <see cref="TryWriteJson"/>
/// reports that case and the caller writes a rejection instead. Names travel as the document spells them —
/// <see cref="WriteStartElement"/> takes the qualified name, exactly as the attributes do — so a prefixed element
/// keeps its prefix and two elements that differ only by prefix stay distinct.
/// </para>
/// </summary>
internal sealed class NdjsonWriter : IDisposable
{
    private const string KindProperty = "t";
    private const string XmlKind = "xml";
    private const string JsonKind = "json";
    private const string EntryKind = "entry";
    private const string RejectedKind = "rejected";
    private const string NameProperty = "n";
    private const string EventProperty = "e";
    private const string StartEvent = "start";
    private const string EndEvent = "end";
    private const string TextEvent = "text";
    private const string ValueProperty = "v";
    private const string AttributesProperty = "a";
    private const string CompressedProperty = "c";
    private const string ExpandedProperty = "x";
    private const string DirectoryProperty = "d";
    private const string CodeProperty = "code";
    private const string ReasonProperty = "reason";
    private const string DoneProperty = "done";
    private const string CountProperty = "count";

    /// <summary>
    /// The first capacity of the per-record buffer. It is grown on demand and reused across records, so a stream of
    /// many small records does not re-allocate, and one huge record pays for its own size once.
    /// </summary>
    private const int BufferBytes = 4096;

    private static readonly JsonWriterOptions Options = new()
    {
        Indented = false,
        SkipValidation = false,

        // The strict encoder: everything outside the ASCII range is escaped, so U+2028, U+2029 and every other
        // separator a consumer might split on stays inside its string instead of ending the line.
        Encoder = JavaScriptEncoder.Default,
    };

    private readonly Stream output;
    private readonly ArrayBufferWriter<byte> buffer = new(BufferBytes);
    private bool finished;

    /// <summary>Wraps one output stream; the caller keeps ownership and disposes it.</summary>
    /// <param name="output">The buffered standard-output stream.</param>
    public NdjsonWriter(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        this.output = output;
    }

    /// <summary>How many records have been written; the marker carries this number.</summary>
    public int Count { get; private set; }

    /// <summary>One XML start-element record with its attributes, in document order.</summary>
    /// <param name="name">
    /// The element's qualified name as the document spells it (the contract's <c>n</c>): a prefixed element keeps
    /// its prefix, exactly as the attributes below do, so nothing in the record loses information the document
    /// carried. A consumer that needs the namespace URI re-reads the artifact.
    /// </param>
    /// <param name="attributes">The element's attributes as written, qualified name and value.</param>
    public void WriteStartElement(string name, List<KeyValuePair<string, string>> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var writer = BeginRecord(XmlKind);
        writer.WriteString(EventProperty, StartEvent);
        writer.WriteString(NameProperty, name);
        writer.WriteStartArray(AttributesProperty);

        foreach (var attribute in attributes)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(attribute.Key);
            writer.WriteStringValue(attribute.Value);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        EndRecord(writer);
    }

    /// <summary>One XML text record; every text-bearing node type travels here.</summary>
    /// <param name="value">The node's value.</param>
    public void WriteText(string value)
    {
        var writer = BeginRecord(XmlKind);
        writer.WriteString(EventProperty, TextEvent);
        writer.WriteString(ValueProperty, value);
        EndRecord(writer);
    }

    /// <summary>One XML end-element record (also emitted for an empty element, so the stream stays balanced).</summary>
    public void WriteEndElement()
    {
        var writer = BeginRecord(XmlKind);
        writer.WriteString(EventProperty, EndEvent);
        EndRecord(writer);
    }

    /// <summary>
    /// One JSON document record: the profile-validated root, compact, or <see langword="false"/> when the writer
    /// cannot represent a value the profile accepted.
    /// </summary>
    /// <param name="root">The document's root element; the caller keeps ownership of its document.</param>
    /// <returns>
    /// True when the record was written; false when a string in the document could not be written, in which case
    /// nothing of the record reached the stream and no record was counted, so the caller is free to emit a
    /// rejection in its place.
    /// </returns>
    public bool TryWriteJson(JsonElement root)
    {
        var writer = BeginRecord(JsonKind);

        try
        {
            writer.WritePropertyName(ValueProperty);
            root.WriteTo(writer);
        }
        catch (InvalidOperationException)
        {
            // JsonElement.WriteTo reads each string value through JsonElement.GetString(), which refuses a lone
            // UTF-16 surrogate ("Cannot read incomplete UTF-16 JSON text as string with missing low surrogate")
            // even though JSON itself accepts the escape \ud800 and the profile's UTF-8 check passes it. The
            // record is still only in this writer's buffer, so it is abandoned whole: no partial line, no count,
            // and the caller reports malformed content instead of letting the run die with exit 3.
            AbandonRecord(writer);

            return false;
        }

        EndRecord(writer);

        return true;
    }

    /// <summary>One archive-entry record: the normalized name and the sizes the walk measured.</summary>
    /// <param name="name">The entry's normalized relative path.</param>
    /// <param name="compressedBytes">The compressed bytes the walk consumed for this entry.</param>
    /// <param name="expandedBytes">The expanded bytes the walk counted for this entry.</param>
    /// <param name="isDirectory">True for a directory entry.</param>
    public void WriteEntry(string name, long compressedBytes, long expandedBytes, bool isDirectory)
    {
        var writer = BeginRecord(EntryKind);
        writer.WriteString(NameProperty, name);
        writer.WriteNumber(CompressedProperty, compressedBytes);
        writer.WriteNumber(ExpandedProperty, expandedBytes);
        writer.WriteBoolean(DirectoryProperty, isDirectory);
        EndRecord(writer);
    }

    /// <summary>
    /// The rejection record for a profile failure: its code and its generic reason word, never its message and
    /// never an artifact byte (00 §5.7 S3).
    /// </summary>
    /// <param name="error">The profile's failure value.</param>
    public void WriteRejected(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        WriteRejected(error.Code, ReasonOf(error));
    }

    /// <summary>
    /// The rejection record for a decision the child made itself: an over-cap stdin, a sniffed mismatch, or a
    /// document holding a string the writer cannot carry (a lone UTF-16 surrogate, which the profile accepts as a
    /// JSON escape and a .NET string cannot hold).
    /// </summary>
    /// <param name="code">The failure code: <c>artifact_invalid</c> or <c>payload_too_large</c>.</param>
    /// <param name="reason">The fixed reason word from the profile's closed set.</param>
    public void WriteRejected(string code, string reason)
    {
        var writer = BeginRecord(RejectedKind);
        writer.WriteString(CodeProperty, code);
        writer.WriteString(ReasonProperty, reason);
        EndRecord(writer);
    }

    /// <summary>
    /// Writes the final marker and flushes everything before returning: from here the stream carries nothing more,
    /// so a consumer that reads on sees EOF straight after the marker's line.
    /// </summary>
    public void WriteDone()
    {
        if (finished)
        {
            throw new InvalidOperationException("the final marker is written once");
        }

        finished = true;

        var writer = new Utf8JsonWriter(buffer, Options);
        writer.WriteStartObject();
        writer.WriteBoolean(DoneProperty, true);
        writer.WriteNumber(CountProperty, Count);
        writer.WriteEndObject();
        writer.Flush();
        writer.Dispose();

        WriteBuffer();
        output.Flush();
    }

    /// <summary>Flushes what has been written; the stream itself stays open for its owner.</summary>
    public void Dispose()
    {
        output.Flush();
        GC.SuppressFinalize(this);
    }

    private Utf8JsonWriter BeginRecord(string kind)
    {
        if (finished)
        {
            throw new InvalidOperationException("no record may follow the final marker");
        }

        // One writer per record, deliberately: a Utf8JsonWriter accepts a single top-level JSON value and refuses
        // the next one ("cannot write the start of an object/array after a single JSON value"), so a stream of
        // records needs a fresh writer each time. The sink is the reused buffer, so the per-record cost is the
        // writer's own small state and the record's bytes, not a growing stream.
        var writer = new Utf8JsonWriter(buffer, Options);
        writer.WriteStartObject();
        writer.WriteString(KindProperty, kind);
        return writer;
    }

    private void EndRecord(Utf8JsonWriter writer)
    {
        writer.WriteEndObject();
        writer.Flush();
        writer.Dispose();

        WriteBuffer();
        Count++;
    }

    /// <summary>
    /// Drops a record that could not be built: the writer's pending bytes go into the buffer and the buffer is
    /// cleared, so the next record starts on a clean buffer and the failed one is never counted. Nothing reached
    /// the stream, because <see cref="WriteBuffer"/> is the only method that writes.
    /// </summary>
    private void AbandonRecord(Utf8JsonWriter writer)
    {
        writer.Dispose();
        buffer.Clear();
    }

    /// <summary>
    /// Copies the finished record's bytes to the stream and ends its line. Nothing but this method writes record
    /// bytes, so a partially built record can never reach stdout: the buffer only leaves here after a trailing
    /// <c>WriteEndObject</c>.
    /// </summary>
    private void WriteBuffer()
    {
        output.Write(buffer.WrittenSpan);
        output.WriteByte((byte)'\n');
        buffer.Clear();
    }

    private static string ReasonOf(Error error)
    {
        if (error.Details is { Count: > 0 } details && !string.IsNullOrEmpty(details[0].Reason))
        {
            return details[0].Reason;
        }

        // Every profile failure carries its reason in details[0] (ParsingFailure.For). A failure without one is
        // this child's bug, not a content verdict: it becomes exit 3 (a crash with no marker), never a made-up
        // reason the launcher would map to a plausible but false outcome.
        throw new InvalidOperationException("the parser profile returned a failure without a reason word");
    }
}
