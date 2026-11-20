using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Unicode;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// What one stdout stream yielded: how many bytes and lines arrived, how many records were accepted (only when the
/// stream was the protocol end to end) and the failure kind that stopped the read early.
/// </summary>
/// <param name="OutputBytes">Every byte the child wrote to stdout, marker and newlines included.</param>
/// <param name="Lines">Every complete line, the marker included.</param>
/// <param name="RecordCount">How many record lines were accepted by the moment the read stopped.</param>
/// <param name="Failure">The failure kind, or <see langword="null"/> when the stream was valid.</param>
internal sealed record ChildOutputCapture(
    long OutputBytes,
    int Lines,
    int RecordCount,
    ParseRunFailureKind? Failure);

/// <summary>
/// Reads and validates the child's stdout protocol (06.3.6 §7) with both the parent's memory and the child's
/// output bounded by construction.
/// <para>
/// The framing is strict and total: every line must be exactly one JSON object, every line must end in <c>\n</c>
/// (a trailing fragment means the stream was truncated, so the marker is not last and the run is malformed), a
/// blank line is malformed, and the last line must be <c>{"done":true,"count":n}</c> with <c>n</c> equal to the
/// number of lines before it — a <c>rejected</c> record counts. The first record carrying a top-level
/// <c>done</c> is the marker; a second one, a marker that is not last, or a record after the marker is
/// malformed, as is a <c>done</c> that is not <see langword="true"/> or a marker without an integer
/// <c>count</c>. Records are otherwise opaque: the reader never interprets a record's content (a record's own
/// <c>count</c> key means nothing).
/// </para>
/// <para>
/// Every accepted record line goes to the caller's <see cref="IParseRecordSink"/> as it is framed, so nothing is
/// kept here: a stream over <c>maxBytes</c> stops the read, a single line longer than <c>maxRecordBytes</c> stops
/// the read (a legit record can carry a whole text node escaped at about 3x, so that cap is derived from the input
/// cap too), and the accepted record count is capped at <c>maxRecords</c>. A line must also be valid UTF-8 and nest
/// no deeper than <see cref="MaxLineDepth"/> — the profile's document depth plus the record wrapper — so a record
/// the child could not have written from an accepted document is malformed, not data. Each stop invokes the
/// caller's failure hook so the runner can kill the child at once instead of waiting for the wall clock while it
/// blocks on a full pipe.
/// </para>
/// </summary>
internal sealed class ChildOutputProtocol
{
    /// <summary>The pipe buffer the reader uses: one read per wake-up, not one per byte.</summary>
    private const int ReadBufferBytes = 64 * 1024;

    /// <summary>The line buffer a record starts with; it grows toward the per-line cap only while a line needs it.</summary>
    private const int InitialLineBytes = 4096;

    /// <summary>
    /// How deep a record may nest: the profile's own document cap (<see cref="ParsingLimits.MaxDepth"/>) plus the
    /// wrapper the child writes around a document — one record object and its value property — and headroom. A JSON
    /// record carries the whole validated document, so a cap below the profile's depth refuses a document the
    /// profile itself accepted; a record deeper than this is not something the child can emit, so it is malformed.
    /// </summary>
    private const int MaxLineDepth = ParsingLimits.MaxDepth + 4;

    /// <summary>The marker's two keys (06.3.6 §7).</summary>
    private const string DoneProperty = "done";

    /// <summary>The marker's record count.</summary>
    private const string CountProperty = "count";

    private static readonly JsonReaderOptions LineReaderOptions = new()
    {
        MaxDepth = MaxLineDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private readonly long maxBytes;
    private readonly int maxRecords;
    private readonly int maxRecordBytes;
    private readonly IParseRecordSink sink;

    /// <summary>Creates a reader bound to one run's caps.</summary>
    /// <param name="maxBytes">The most stdout bytes the run may write (positive).</param>
    /// <param name="maxRecords">The most lines the run may write, the marker included (positive).</param>
    /// <param name="maxRecordBytes">The longest single line (positive).</param>
    /// <param name="sink">Where each accepted record line goes.</param>
    public ChildOutputProtocol(long maxBytes, int maxRecords, int maxRecordBytes, IParseRecordSink sink)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordBytes);
        ArgumentNullException.ThrowIfNull(sink);

        this.maxBytes = maxBytes;
        this.maxRecords = maxRecords;
        this.maxRecordBytes = maxRecordBytes;
        this.sink = sink;
    }

    /// <summary>
    /// The capture to return for a stream the parent had to abandon. The stdout never ended because a descendant of
    /// the child inherited the write end: no marker can arrive, so the run is malformed and is never read as a
    /// success. The counters are zero — what the abandoned reader had seen is inside that reader — because the
    /// verdict is the stream's absence, not its byte count.
    /// </summary>
    /// <returns>The capture to return for a stream the parent had to abandon.</returns>
    public ChildOutputCapture Abandoned() =>
        new Accumulator(maxBytes, maxRecords, maxRecordBytes, sink).FailedAtTransport();

    /// <summary>Reads the child's stdout until the stream ends or a cap or a protocol violation stops the read.</summary>
    /// <param name="stdout">The child's stdout pipe; the caller keeps ownership.</param>
    /// <param name="onProtocolFailure">Called once when the stream must be abandoned because it is unusable.</param>
    /// <returns>The capture the verdict is read from.</returns>
    public async Task<ChildOutputCapture> ReadAsync(Stream stdout, Action onProtocolFailure)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(onProtocolFailure);

        var state = new Accumulator(maxBytes, maxRecords, maxRecordBytes, sink);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);

        try
        {
            while (true)
            {
                int read;

                try
                {
                    read = await stdout.ReadAsync(buffer.AsMemory(0, ReadBufferBytes)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    // The pipe died before the marker: there is no verdict to read, so the run is malformed.
                    return state.FailedAtTransport();
                }

                if (read == 0)
                {
                    return state.Complete();
                }

                if (state.Accept(buffer, read, out var early))
                {
                    onProtocolFailure();

                    return early;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static LineKind Classify(ReadOnlySpan<byte> line, out long count)
    {
        count = 0;
        var reader = new Utf8JsonReader(line, LineReaderOptions);

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return LineKind.Invalid;
            }

            var doneSeen = false;
            var countSeen = false;
            var sawEnd = false;
            long markerCount = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    sawEnd = true;

                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return LineKind.Invalid;
                }

                var isDone = reader.ValueTextEquals(DoneProperty);
                var isCount = !isDone && reader.ValueTextEquals(CountProperty);

                if (!reader.Read())
                {
                    return LineKind.Invalid;
                }

                if (isDone)
                {
                    if (doneSeen || reader.TokenType != JsonTokenType.True)
                    {
                        return LineKind.Invalid;
                    }

                    doneSeen = true;
                }
                else if (isCount)
                {
                    if (countSeen || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out markerCount))
                    {
                        return LineKind.Invalid;
                    }

                    countSeen = true;
                }
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }

            if (!sawEnd)
            {
                return LineKind.Invalid;
            }

            // One JSON value per line: reading past the top-level object only yields false when what follows is
            // whitespace, and throws when it is anything else.
            if (reader.Read())
            {
                return LineKind.Invalid;
            }

            if (!doneSeen)
            {
                return LineKind.Record;
            }

            if (!countSeen)
            {
                return LineKind.Invalid;
            }

            count = markerCount;

            return LineKind.Marker;
        }
        catch (JsonException)
        {
            return LineKind.Invalid;
        }
        catch (InvalidOperationException)
        {
            return LineKind.Invalid;
        }
    }

    private enum LineKind
    {
        /// <summary>One JSON object that is not the marker.</summary>
        Record,

        /// <summary>The final marker, with its count in <c>count</c>.</summary>
        Marker,

        /// <summary>Not one JSON object, or a <c>done</c>/<c>count</c> that breaks the marker's shape.</summary>
        Invalid,
    }

    /// <summary>The read's whole mutable state; split out so the async loop never holds a <see cref="Span{T}"/> across an await.</summary>
    private sealed class Accumulator
    {
        private readonly long maxBytes;
        private readonly int maxRecords;
        private readonly int maxRecordBytes;
        private readonly IParseRecordSink sink;

        private byte[] line;
        private int lineLength;
        private long bytes;
        private int lines;
        private int records;
        private bool markerSeen;

        public Accumulator(long maxBytes, int maxRecords, int maxRecordBytes, IParseRecordSink sink)
        {
            this.maxBytes = maxBytes;
            this.maxRecords = maxRecords;
            this.maxRecordBytes = maxRecordBytes;
            this.sink = sink;
            line = new byte[Math.Min(maxRecordBytes, InitialLineBytes)];
        }

        /// <summary>Consumes one block of stdout bytes, completing the lines it carries.</summary>
        /// <param name="buffer">The rented read buffer.</param>
        /// <param name="count">How many of its leading bytes are valid.</param>
        /// <param name="early">The capture to return when the read must stop now; non-null when this returns true.</param>
        /// <returns>True when the read must stop; <paramref name="early"/> then holds the result.</returns>
        public bool Accept(byte[] buffer, int count, [NotNullWhen(true)] out ChildOutputCapture? early)
        {
            early = null;
            bytes += count;

            if (bytes > maxBytes)
            {
                early = Failed(ParseRunFailureKind.OutputBytesExceeded);

                return true;
            }

            var span = buffer.AsSpan(0, count);

            while (!span.IsEmpty)
            {
                var newline = span.IndexOf((byte)'\n');
                var take = newline < 0 ? span.Length : newline;

                if (Append(span[..take]) is { } appendFailure)
                {
                    early = appendFailure;

                    return true;
                }

                if (newline < 0)
                {
                    return false;
                }

                if (CompleteLine() is { } lineFailure)
                {
                    early = lineFailure;

                    return true;
                }

                span = span[(newline + 1)..];
            }

            return false;
        }

        /// <summary>The end of the stream: valid only when a marker closed a newline-terminated stream.</summary>
        /// <returns>The capture the runner reads its verdict from.</returns>
        public ChildOutputCapture Complete()
        {
            if (lineLength > 0 || !markerSeen)
            {
                return Failed(ParseRunFailureKind.MalformedOutput);
            }

            return new ChildOutputCapture(bytes, lines, records, null);
        }

        /// <summary>The pipe broke before the stream ended: no marker can arrive, so the run is malformed.</summary>
        /// <returns>The capture to return.</returns>
        public ChildOutputCapture FailedAtTransport() => Failed(ParseRunFailureKind.MalformedOutput);

        private ChildOutputCapture Failed(ParseRunFailureKind failure) =>
            new(bytes, lines, records, failure);

        private ChildOutputCapture? Append(ReadOnlySpan<byte> bytesToAppend)
        {
            if (bytesToAppend.IsEmpty)
            {
                return null;
            }

            var required = lineLength + bytesToAppend.Length;

            // A negative sum is an int overflow, which can only mean the line is far past any cap.
            if (required < 0 || required > maxRecordBytes)
            {
                return Failed(ParseRunFailureKind.OutputBytesExceeded);
            }

            if (required > line.Length)
            {
                Grow(required);
            }

            bytesToAppend.CopyTo(line.AsSpan(lineLength));
            lineLength = required;

            return null;
        }

        private ChildOutputCapture? CompleteLine()
        {
            lines++;

            if (lines > maxRecords)
            {
                return Failed(ParseRunFailureKind.OutputRecordsExceeded);
            }

            var text = line.AsSpan(0, lineLength);

            if (text.IsEmpty)
            {
                return Failed(ParseRunFailureKind.MalformedOutput);
            }

            // Invalid UTF-8 is not a record. The reader below scans strings without decoding them, so a byte the
            // child's own writer cannot emit would otherwise reach a sink as a record it cannot decode; and the
            // sink only ever sees the child's own bytes, so nothing downstream could repair it.
            if (!Utf8.IsValid(text))
            {
                return Failed(ParseRunFailureKind.MalformedOutput);
            }

            var kind = Classify(text, out var count);

            if (kind == LineKind.Invalid)
            {
                return Failed(ParseRunFailureKind.MalformedOutput);
            }

            if (kind == LineKind.Marker)
            {
                // A second marker, or one that does not count the records before it, is a crash, not a success.
                if (markerSeen || records != count)
                {
                    return Failed(ParseRunFailureKind.MalformedOutput);
                }

                markerSeen = true;
                lineLength = 0;

                return null;
            }

            if (markerSeen)
            {
                return Failed(ParseRunFailureKind.MalformedOutput);
            }

            sink.Accept(text);
            records++;
            lineLength = 0;

            return null;
        }

        private void Grow(int required)
        {
            var size = (int)Math.Min(maxRecordBytes, Math.Max(required, (long)line.Length * 2));
            var grown = new byte[size];
            line.AsSpan(0, lineLength).CopyTo(grown);
            line = grown;
        }
    }
}
