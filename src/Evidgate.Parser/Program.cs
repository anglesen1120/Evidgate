using System.Diagnostics.CodeAnalysis;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Microsoft.Win32.SafeHandles;

namespace Evidgate.Parser;

/// <summary>
/// The <c>Evidgate.Parser</c> child entry point (06.3.6 §5, §7): reads one artifact's bytes from stdin, runs the
/// 06.3.1 parser profile over them and writes the NDJSON entry stream with a final
/// <c>{"done":true,"count":n}</c> marker. It is the one process that touches untrusted bytes, so it reads only
/// stdin, writes only stdout/stderr, opens no file, reads no environment variable and has no network code at all
/// (06.3.6 AC1, AC4). It applies no limit itself: the parent runner (T1.3) passes <c>DOTNET_GCHeapHardLimit</c> in
/// this process's environment, kills it on the wall clock and applies or arranges the CPU and address-space
/// rlimits around it.
/// <para>
/// The profile is not re-implemented here: the child links the profile sources, so the DTD scan, the depth cap,
/// the byte cap, the 30 s timeout and the zip-slip rules are the same code the in-process caller runs. This file
/// only chooses the arm, caps the stdin read, streams what the profile produced and owns the exit-code contract:
/// 0 for a streamed result or rejection, 2 for a usage error, 3 for an unexpected internal error — with fixed
/// words on stderr and never an artifact byte, a name or an exception message (00 §5.7 S3).
/// </para>
/// <para>
/// The marker's <c>count</c> is the number of records written before it, and a <c>rejected</c> record is counted
/// like any other: an empty stdin emits one rejection and then <c>{"done":true,"count":1}</c>. A run that cannot
/// finish the stream — a closed stdout, an exception — writes no marker at all and exits 3, which is exactly how
/// the parent tells "the child judged the artifact" from "the child died" (AC3).
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>Exit 0: a result or a rejection was streamed and the final marker written.</summary>
    private const int ExitStreamed = 0;

    /// <summary>Exit 2: the command line is not the child's contract; nothing was written to stdout.</summary>
    private const int ExitUsage = 2;

    /// <summary>Exit 3: an unexpected internal error; the marker is missing, so the launcher maps a crash.</summary>
    private const int ExitInternal = 3;

    /// <summary>The one usage line; a fixed string, so it can never echo an argument.</summary>
    private const string UsageText =
        "evidgate-parser: usage: Evidgate.Parser --profile xml|json|archive --max-bytes <bytes> [--media-type <type>]";

    /// <summary>The one internal-error line; a fixed string, so it can never echo an exception.</summary>
    private const string InternalText = "evidgate-parser: internal error";

    /// <summary>
    /// The artifact failure code recorded when the leading bytes contradict the declared media type
    /// (<c>ArtifactFailureCode.MediaTypeMismatch</c>, <c>Domain/Artifact.cs</c>). The profile's
    /// <see cref="ParsingFailureReason"/> has no mismatch member because in-process the sniff is the completion
    /// verifier's step, not <see cref="IParsing"/>'s; the child reports the same word the artifact row carries.
    /// </summary>
    private const string MediaTypeMismatchReason = "media_type_mismatch";

    /// <summary>Buffered output size: one record is far smaller, so a run flushes to the pipe a handful of times.</summary>
    private const int StdoutBufferBytes = 64 * 1024;

    /// <summary>The standard-output descriptor every Unix process starts with (POSIX; not a Windows handle).</summary>
    private const int StandardOutputDescriptor = 1;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The child's whole contract is that no exception escapes: exit 3 with one fixed word, and "
            + "never an exception message, a type name or an artifact byte on stderr (06.3.6 §7, AC3).")]
    private static int Main(string[] args)
    {
        if (!ChildOptions.TryParse(args, out var options))
        {
            Console.Error.WriteLine(UsageText);

            return ExitUsage;
        }

        try
        {
            return Run(options);
        }
        catch (Exception)
        {
            // The profile's own 30 s cap becomes a `timeout` rejection inside the profile, and a cancellation that
            // reaches the entry point is the parent's kill or a stream fault, which is a crash from here. A crash
            // writes no marker, which is exactly how the parent tells "the child died" from "the child judged the
            // artifact": a missing, repeated, non-last or miscounted marker is `parse_crashed`.
            Console.Error.WriteLine(InternalText);

            return ExitInternal;
        }
    }

    private static int Run(ChildOptions options)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = new BufferedStream(OpenStandardOutput(), StdoutBufferBytes);
        using var writer = new NdjsonWriter(stdout);

        var content = StdinBuffer.Read(stdin, options.MaxBytes);

        if (content.Overflowed)
        {
            // One byte past the cap is a size rejection, not a truncated parse: the parent's pipe may still hold
            // the rest, and the child never reads it.
            writer.WriteRejected(ParsingErrorCode.PayloadTooLarge, ParsingFailureReason.SizeExceeded);
        }
        else
        {
            var parsing = new ParsingService();

            if (options.MediaType is { } declared
                && parsing.SniffMediaType(declared, content.Prefix) == MediaSniffVerdict.Mismatch)
            {
                // Fail closed, exactly as the completion verifier does: bytes that contradict their declared
                // type are never parsed.
                writer.WriteRejected(ParsingErrorCode.ArtifactInvalid, MediaTypeMismatchReason);
            }
            else
            {
                Emit(parsing, options, content, writer);
            }
        }

        writer.WriteDone();

        return ExitStreamed;
    }

    private static void Emit(IParsing parsing, ChildOptions options, StdinBuffer content, NdjsonWriter writer)
    {
        switch (options.Profile)
        {
            case ParseProfile.Xml:
                EmitXml(parsing, content, options.MaxBytes, writer);
                break;

            case ParseProfile.Json:
                EmitJson(parsing, content, options.MaxBytes, writer);
                break;

            default:
                EmitArchive(parsing, content, options.MaxBytes, writer);
                break;
        }
    }

    /// <summary>
    /// Runs the profile's one pass over the child's bounded stdin copy and, on every path — a streamed result, a
    /// rejection or a fault — disposes that read stream and drops the copy before returning.
    /// <see cref="StdinBuffer.Release"/> clears the chunks the stream reads, so the two belong together, and both
    /// happen before the emit walk starts: every arm's result keeps the profile's own copy of the bytes
    /// (<c>SafeXml</c> its chunks, <c>SafeJson</c> its contiguous buffer, <c>SafeZipArchive</c> its buffer) and
    /// reads nothing from this stream after the pass returns, so the walk and the marker run with one copy of the
    /// input live, not two (the parent's heap hard limit is sized on that).
    /// </summary>
    /// <typeparam name="TReport">What the profile materialised from the bytes.</typeparam>
    /// <param name="content">The child's bounded stdin copy; released when this returns.</param>
    /// <param name="pass">The profile pass, run over a fresh read stream of the content.</param>
    /// <returns>The profile's outcome, no longer tied to the input copy.</returns>
    private static Result<TReport> Parse<TReport>(StdinBuffer content, Func<Stream, Result<TReport>> pass)
    {
        try
        {
            using var stream = content.OpenStream();

            return pass(stream);
        }
        finally
        {
            content.Release();
        }
    }

    /// <summary>
    /// The standard-output stream, opened so that writing to a closed reader is an <see cref="IOException"/> rather
    /// than a silent success. Measured on this host: <see cref="Console.OpenStandardOutput"/> swallows the broken
    /// pipe on Unix — the write reports success and the bytes are dropped — so a parent that closed the pipe would
    /// see exit 0 with no marker, which is indistinguishable from a truncated success; a <see cref="FileStream"/>
    /// over the same descriptor reports the EPIPE, and the run ends as exit 3 with no marker (06.3.6 §7, AC3).
    /// </summary>
    private static Stream OpenStandardOutput()
    {
        // Windows' console stream already surfaces a broken pipe, and its standard handles are not Unix descriptors.
        if (OperatingSystem.IsWindows())
        {
            return Console.OpenStandardOutput();
        }

        // Unbuffered: the BufferedStream above already batches 64 KiB per write, so a second buffer would only
        // delay the failure the broken pipe has to report.
        return new FileStream(new SafeFileHandle((nint)StandardOutputDescriptor, ownsHandle: false), FileAccess.Write, bufferSize: 0);
    }

    /// <summary>
    /// Streams one validated XML report as start/text/end records. The profile has already walked the document
    /// once under the depth, entity and timeout guards; this second walk only reads what that walk accepted, and
    /// every name and value travels as a JSON string, so no artifact byte can reach the framing (06.3.6 §7).
    /// <para>
    /// Unlike the JSON arm this needs no "the writer cannot carry this value" guard, and every clause here was
    /// measured on the built child: a document carrying an unpaired surrogate
    /// (<c>&lt;r&gt;&amp;#xD800;&lt;/r&gt;</c>, and the <c>&amp;#xDC00;</c> twin) comes back as
    /// <c>artifact_invalid</c>/<c>malformed</c> before this walk starts, and a well-formed pair
    /// (<c>&amp;#x1F600;</c>) travels as one escaped character. The guard exists on the JSON arm because the
    /// writer refuses what the profile accepted there — likewise measured: without it the run died with exit 3
    /// and wrote nothing, as a lone surrogate in a JSON string reaches <c>JsonElement.WriteTo</c>.
    /// </para>
    /// </summary>
    private static void EmitXml(IParsing parsing, StdinBuffer content, long maxBytes, NdjsonWriter writer)
    {
        // The profile's own ParsingLimits.ParseTimeout is the only timer: a second one here would race it and
        // could report `timeout` for a parse that finished.
        var result = Parse(content, stream => parsing.ParseXmlReport(stream, maxBytes, CancellationToken.None));

        if (!result.IsSuccess)
        {
            writer.WriteRejected(result.Error!);

            return;
        }

        using var reader = result.Value.OpenReader();
        var attributes = new List<KeyValuePair<string, string>>();

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var isEmpty = reader.IsEmptyElement;
                    ReadAttributes(reader, attributes);

                    // The qualified name, as the document spells it: a prefixed element keeps its prefix, exactly
                    // as its attributes do below, so no record loses what the document carried.
                    writer.WriteStartElement(reader.Name, attributes);
                    if (isEmpty)
                    {
                        // A self-closing element raises no EndElement node: emit its end here so the stream rebuilds the document.
                        writer.WriteEndElement();
                    }

                    break;

                case XmlNodeType.EndElement:
                    writer.WriteEndElement();
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    // One field, three node types: the value the document carries is the same, and keeping the
                    // whitespace nodes means the record stream rebuilds the document without losing text.
                    writer.WriteText(reader.Value);
                    break;

                default:
                    // The declaration, comments and processing instructions carry no report data, and
                    // DtdProcessing.Prohibit means a document type never reaches this reader.
                    break;
            }
        }
    }

    /// <summary>
    /// Emits one validated JSON payload as a single record carrying the document root. A root the writer cannot
    /// represent — a string holding a lone UTF-16 surrogate, which JSON accepts as an escape (<c>"\ud800"</c>) and
    /// the profile's strict UTF-8 check passes, but a .NET string cannot hold — becomes a malformed rejection
    /// instead of an exit-3 crash: the record is abandoned before any byte of it reaches stdout.
    /// </summary>
    private static void EmitJson(IParsing parsing, StdinBuffer content, long maxBytes, NdjsonWriter writer)
    {
        var result = Parse(content, stream => parsing.ParseJson(stream, maxBytes, CancellationToken.None));

        if (!result.IsSuccess)
        {
            writer.WriteRejected(result.Error!);

            return;
        }

        using var document = result.Value;

        if (!writer.TryWriteJson(document.RootElement))
        {
            writer.WriteRejected(ParsingErrorCode.ArtifactInvalid, ParsingFailureReason.Malformed);
        }
    }

    /// <summary>
    /// Emits one entry record per archive entry, in archive order. The profile has already walked the whole
    /// archive — declared count, every normalized name, duplicate names, symbolic links, the expanded budget and
    /// each entry's ratio — so a rejected archive produces no entry record at all.
    /// </summary>
    private static void EmitArchive(IParsing parsing, StdinBuffer content, long maxBytes, NdjsonWriter writer)
    {
        var result = Parse(content, stream => parsing.EnumerateArchive(stream, maxBytes, CancellationToken.None));

        if (!result.IsSuccess)
        {
            writer.WriteRejected(result.Error!);

            return;
        }

        foreach (var entry in result.Value)
        {
            writer.WriteEntry(entry.Name, entry.CompressedBytes, entry.ExpandedBytes, entry.IsDirectory);
        }
    }

    /// <summary>
    /// Copies the current element's attributes into <paramref name="attributes"/>, in document order, and leaves
    /// the reader on the element again. The qualified name is used as written: two attributes that differ only by
    /// their prefix stay distinct, so the record loses nothing.
    /// </summary>
    private static void ReadAttributes(XmlReader reader, List<KeyValuePair<string, string>> attributes)
    {
        attributes.Clear();

        if (!reader.MoveToFirstAttribute())
        {
            return;
        }

        do
        {
            attributes.Add(new KeyValuePair<string, string>(reader.Name, reader.Value));
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
    }
}
