using System.Runtime.InteropServices;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// Reads the text of a DOCX package, only through the parser profile: the zip walk (entry, expansion, ratio and
/// nesting caps) is <see cref="IParsing.ReadArchiveEntries"/> and every XML part is parsed by
/// <see cref="IParsing.ParseXmlReport"/>, so a DTD or entity in any part refuses the file and no second
/// hardening copy exists here. Only metadata and XML parts are ever kept; media and binary parts are counted
/// by the walk and dropped.
/// </summary>
internal static class DocxTextReader
{
    private const string DocumentPart = "word/document.xml";
    private const string FootnotesPart = "word/footnotes.xml";
    private const string ContentTypesPart = "[Content_Types].xml";
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string StrictWordNamespace = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const long MaxExpandedBytes = 64L * 1024 * 1024;
    private const long MaxPartBytes = 32L * 1024 * 1024;
    private const int NodesPerDeadlineCheck = 256;

    /// <summary>Extracts the body and footnote text of one DOCX.</summary>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="document">The package bytes, already bounded and sniffed.</param>
    /// <param name="caps">The caps of this intake.</param>
    /// <param name="deadline">The wall-clock budget.</param>
    /// <returns>The raw text; the caller normalises it.</returns>
    /// <exception cref="ExtractionRefusedException">The package breaks a cap or a safety rule.</exception>
    public static string Read(IParsing parsing, byte[] document, IntakeCaps caps, ExtractionDeadline deadline)
    {
        deadline.ThrowIfExpired();

        using var stream = new MemoryStream(document, writable: false);
        var archive = parsing.ReadArchiveEntries(
            stream,
            document.LongLength,
            new ArchiveCaps(caps.MaxZipEntries, MaxExpandedBytes, caps.MaxCompressionRatio) { RejectNestedArchives = true },
            IsXmlPart,
            MaxPartBytes,
            deadline.Token);

        if (!archive.IsSuccess)
        {
            throw new ExtractionRefusedException(MapFailure(archive.Error!));
        }

        var contents = archive.Value;
        RefuseMacroOrEmbeddedPackage(contents);

        var parts = ParseXmlParts(parsing, contents, deadline, out var contentTypes);

        RefuseMacroContentType(contentTypes);

        if (!parts.TryGetValue(DocumentPart, out var body))
        {
            throw new ExtractionRefusedException(IntakeFailureCode.ExtractionFailed);
        }

        var text = new TextAccumulator(caps.MaxExtractedChars);
        ReadText(body, text, deadline);

        if (parts.TryGetValue(FootnotesPart, out var footnotes))
        {
            text.BreakLine();
            ReadText(footnotes, text, deadline);
        }

        return text.ToString();
    }

    private static bool IsXmlPart(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    /// <summary>A VBA project in any directory, or any embedded object package, is refused from the entry names alone.</summary>
    private static void RefuseMacroOrEmbeddedPackage(ArchiveContents contents)
    {
        var embedded = false;

        foreach (var entry in contents.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var separator = entry.Name.LastIndexOf('/');
            var fileName = separator < 0 ? entry.Name : entry.Name[(separator + 1)..];

            if (fileName.StartsWith("vbaProject", StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionRefusedException(IntakeFailureCode.MacroPresent);
            }

            embedded |= entry.Name.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase);
        }

        if (embedded)
        {
            throw new ExtractionRefusedException(IntakeFailureCode.ArchiveLimitExceeded);
        }
    }

    /// <summary>Parses every XML part through the profile, so a DTD anywhere refuses the file, and checks each relationships part.</summary>
    private static Dictionary<string, ParsedXmlReport> ParseXmlParts(
        IParsing parsing,
        ArchiveContents contents,
        ExtractionDeadline deadline,
        out ParsedXmlReport contentTypes)
    {
        var parts = new Dictionary<string, ParsedXmlReport>(StringComparer.OrdinalIgnoreCase);
        ParsedXmlReport? declared = null;

        foreach (var entry in contents.Entries)
        {
            if (entry.IsDirectory || !IsXmlPart(entry.Name) || !contents.TryGetEntry(entry.Name, out var bytes))
            {
                continue;
            }

            deadline.ThrowIfExpired();

            using var stream = StreamOver(bytes);
            var parsed = parsing.ParseXmlReport(stream, bytes.Length, deadline.Token);

            if (!parsed.IsSuccess)
            {
                throw new ExtractionRefusedException(MapFailure(parsed.Error!));
            }

            if (entry.Name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                RefuseExternalRelationship(parsed.Value);
            }
            else if (string.Equals(entry.Name, ContentTypesPart, StringComparison.OrdinalIgnoreCase))
            {
                declared = parsed.Value;
            }

            parts[entry.Name] = parsed.Value;
        }

        contentTypes = declared ?? throw new ExtractionRefusedException(IntakeFailureCode.ExtractionFailed);

        return parts;
    }

    /// <summary>
    /// A relationship that points outside the package is refused, except a plain hyperlink: a link is only text to
    /// this reader and is never fetched, and requirement documents carry them routinely.
    /// </summary>
    private static void RefuseExternalRelationship(ParsedXmlReport relationships)
    {
        using var reader = relationships.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "Relationship", StringComparison.Ordinal)
                || !string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var type = reader.GetAttribute("Type") ?? string.Empty;

            if (!type.EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionRefusedException(IntakeFailureCode.ExternalReferencePresent);
            }
        }
    }

    /// <summary>A content type that declares a macro-enabled document or a VBA project refuses the file.</summary>
    private static void RefuseMacroContentType(ParsedXmlReport contentTypes)
    {
        using var reader = contentTypes.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var contentType = reader.GetAttribute("ContentType");

            if (contentType is not null
                && (contentType.Contains("macroenabled", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("vbaproject", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ExtractionRefusedException(IntakeFailureCode.MacroPresent);
            }
        }
    }

    /// <summary>
    /// Walks one WordprocessingML part: text of <c>t</c> runs, tabs and breaks. Deleted text, field codes and every
    /// other element are skipped. Text is copied in chunks so one huge run cannot exceed the cap before it is checked.
    /// </summary>
    private static void ReadText(ParsedXmlReport part, TextAccumulator text, ExtractionDeadline deadline)
    {
        using var reader = part.OpenReader();
        var buffer = new char[4096];
        var inRun = false;
        var nodes = 0;

        while (reader.Read())
        {
            if (++nodes % NodesPerDeadlineCheck == 0)
            {
                deadline.ThrowIfExpired();
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Element when IsWordElement(reader):
                    inRun = StartElement(reader, text);
                    break;
                case XmlNodeType.EndElement when IsWordElement(reader):
                    inRun = false;
                    EndElement(reader, text);
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace
                    when inRun:
                    int read;
                    while ((read = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                    {
                        text.Append(buffer.AsSpan(0, read));
                    }

                    break;
            }
        }
    }

    /// <summary>Handles a start element; returns true while the reader is inside a text run.</summary>
    private static bool StartElement(XmlReader reader, TextAccumulator text)
    {
        switch (reader.LocalName)
        {
            case "t":
                return !reader.IsEmptyElement;
            case "tab":
                text.Append('\t');
                break;
            case "br" or "cr":
                text.BreakLine();
                break;
            case "noBreakHyphen":
                text.Append('-');
                break;
            case "p" when reader.IsEmptyElement:
                text.BreakLine();
                break;
        }

        return false;
    }

    private static void EndElement(XmlReader reader, TextAccumulator text)
    {
        if (reader.LocalName == "p")
        {
            text.BreakLine();
        }
    }

    private static bool IsWordElement(XmlReader reader) =>
        reader.NamespaceURI is WordNamespace or StrictWordNamespace;

    /// <summary>A read-only stream over the profile's buffer, without copying it.</summary>
    private static MemoryStream StreamOver(ReadOnlyMemory<byte> part) =>
        MemoryMarshal.TryGetArray(part, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(part.ToArray(), writable: false);

    /// <summary>Maps a profile failure to the closed intake codes; nothing of the failure's own text is kept.</summary>
    private static string MapFailure(Error error)
    {
        if (error.Code == ParsingErrorCode.PayloadTooLarge)
        {
            return IntakeFailureCode.ArchiveLimitExceeded;
        }

        var reason = error.Details is { Count: > 0 } details ? details[0].Reason : null;

        return reason switch
        {
            ParsingFailureReason.SizeExceeded
                or ParsingFailureReason.EntryCountExceeded
                or ParsingFailureReason.CompressionRatioExceeded
                or ParsingFailureReason.NestingExceeded
                or ParsingFailureReason.DepthExceeded => IntakeFailureCode.ArchiveLimitExceeded,
            ParsingFailureReason.DtdProhibited => IntakeFailureCode.ExternalReferencePresent,
            ParsingFailureReason.Timeout => IntakeFailureCode.ExtractionTimeout,
            _ => IntakeFailureCode.ExtractionFailed,
        };
    }
}
