using System.Diagnostics.CodeAnalysis;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// Reads one XLSX body into the shared row table (13.1.1 AC13/AC14/AC16, T5.2): the archive walk and every XML part
/// go through the 06.3.1 parser profile, the first worksheet is resolved through the workbook and its relationships,
/// and its cells become the same <c>authoring-import.v1</c> document the JSON body carries — so the schema, the row
/// rules and the text hygiene of the other two formats are the ones this format gets
/// (<see cref="ImportSchema"/> and <see cref="ImportRows"/> are the one implementation of them).
/// </summary>
/// <remarks>
/// <para>
/// The order is deliberate: the archive is walked under the AC16 caps first, then a macro part is refused from the
/// entry metadata and the content types, and only then are the workbook, its relationships and the worksheet parsed
/// — so a hostile package is refused before any sheet is examined, and every reason names the guard
/// (<c>zip_size_exceeded</c>, <c>zip_entries_exceeded</c>, <c>zip_ratio_exceeded</c>, <c>dtd_prohibited</c>,
/// <c>macro_prohibited</c>, <c>unsupported_format</c>) rather than a cell (S3).
/// </para>
/// <para>
/// Cell types this reader accepts: <c>t="s"</c> (a shared-string index), <c>t="inlineStr"</c> (an <c>is</c> run),
/// <c>t="str"</c> and <c>t="n"</c>; styles are ignored, and a boolean, error or date cell is a <c>value_invalid</c>
/// refusal because the flat table has no column for it. Every cell is text except the numeric
/// <c>position</c> column, and a text cell is unescaped with <see cref="XlsxFormat.Unescape"/> before the shared row
/// rules see it, so a workbook this module wrote is read back exactly.
/// </para>
/// </remarks>
internal static partial class XlsxReader
{
    /// <summary>The AC13 reason for a file the XLSX carrier cannot represent (a broken part graph, a missing header).</summary>
    private const string UnsupportedFormat = "unsupported_format";

    /// <summary>The AC13 reason for one cell that breaks its column's rule.</summary>
    private const string ValueInvalid = "value_invalid";

    /// <summary>The AC16 reason for an archive over the expanded-size cap or a part over its own cap.</summary>
    private const string ZipSizeExceeded = "zip_size_exceeded";

    /// <summary>The AC16 reason for an archive over the entry cap.</summary>
    private const string ZipEntriesExceeded = "zip_entries_exceeded";

    /// <summary>The AC16 reason for an entry over the compression-ratio cap.</summary>
    private const string ZipRatioExceeded = "zip_ratio_exceeded";

    /// <summary>The AC16 reason for a part that declares or reaches a DTD or an external entity.</summary>
    private const string DtdProhibited = "dtd_prohibited";

    /// <summary>The AC16 reason for a package carrying a macro part or a macro-enabled content type.</summary>
    private const string MacroProhibited = "macro_prohibited";

    /// <summary>The pointer a cell beyond the thirteen columns carries; the header has no name for it (S3).</summary>
    private const string ExtraColumnPath = "/extra_column";

    /// <summary>
    /// The most characters one element's text is allowed to accumulate: one more than a cell may hold, so a value over
    /// the cell cap is recognised after a single character past it instead of after the whole element is built
    /// (<see cref="ImportText.MaxCellChars"/> is the cap the row rules apply).
    /// </summary>
    private const int TextCharLimit = ImportText.MaxCellChars + 1;

    /// <summary>
    /// The most characters the whole shared-string table may accumulate: the selected part's byte cap, which no part
    /// this reader accepts can exceed in characters, because every character of the table came from at least one byte
    /// of the part. It is the explicit statement of that bound, so raising a cap cannot silently unbound the table.
    /// </summary>
    private const long MaxSharedStringChars = XlsxFormat.MaxPartBytes;

    /// <summary>The file suffix of the macro part the corpus and every Excel writer use.</summary>
    private const string MacroPartFileName = "vbaproject.bin";

    /// <summary>The column of the <c>steps</c> cell, which carries a nested JSON document.</summary>
    private static readonly int StepsColumn = Array.IndexOf(ImportSchema.ColumnNames, "steps");

    /// <summary>Reads one XLSX body (13.1.1 AC13/AC16).</summary>
    /// <param name="body">The already-bounded request body.</param>
    /// <param name="parsing">The parser profile the archive walk and every part go through.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rows, the refusals, or the 413 verdict for a file over the row cap.</returns>
    internal static ImportParse Read(byte[] body, IParsing parsing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(parsing);

        // An empty body is not an archive and the profile's caps are positive by contract, so the refusal is decided
        // here rather than by a cap of zero bytes.
        if (body.Length == 0)
        {
            return ImportParse.Invalid([FileIssue(UnsupportedFormat)]);
        }

        var archive = ReadArchive(body, parsing, cancellationToken);

        if (!archive.IsSuccess)
        {
            return ImportParse.Invalid([MapFailure(archive.Error!)]);
        }

        var contents = archive.Value;

        // The macro guard is metadata-only and runs before any part is parsed (AC16): a package that carries a VBA
        // project is refused whether or not its content types admit it.
        if (HasMacroPart(contents))
        {
            return ImportParse.Invalid([FileIssue(MacroProhibited)]);
        }

        if (!contents.TryGetEntry(XlsxFormat.ContentTypesPart, out var contentTypesPart))
        {
            return ImportParse.Invalid([FileIssue(UnsupportedFormat)]);
        }

        if (!TryParsePart(parsing, contentTypesPart, cancellationToken, out var contentTypes, out var contentTypesIssue))
        {
            return ImportParse.Invalid([contentTypesIssue]);
        }

        if (DeclaresMacro(contentTypes))
        {
            return ImportParse.Invalid([FileIssue(MacroProhibited)]);
        }

        if (!TryResolveWorksheet(contents, parsing, cancellationToken, out var worksheet, out var worksheetIssue))
        {
            return ImportParse.Invalid([worksheetIssue]);
        }

        var sharedStrings = ReadSharedStrings(contents, parsing, cancellationToken, out var sharedStringsIssue);

        if (sharedStringsIssue is not null)
        {
            return ImportParse.Invalid([sharedStringsIssue]);
        }

        return ReadSheet(parsing, worksheet, sharedStrings, cancellationToken);
    }

    /// <summary>Walks the archive under the AC16 caps, through the profile, and keeps the parts a workbook needs.</summary>
    /// <param name="body">The archive bytes.</param>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The accepted archive, or the profile failure this reader maps to an AC16 reason.</returns>
    private static Result<ArchiveContents> ReadArchive(byte[] body, IParsing parsing, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(body, writable: false);

        return parsing.ReadArchiveEntries(
            content,
            body.LongLength,
            new ArchiveCaps(
                ImportLimits.MaxXlsxEntries,
                ImportLimits.MaxXlsxExpandedBytes,
                ImportLimits.MaxXlsxCompressionRatio),
            XlsxFormat.IsSelectedPart,
            XlsxFormat.MaxPartBytes,
            cancellationToken);
    }

    /// <summary>True when the archive carries a VBA project part, in any directory and any casing.</summary>
    /// <param name="contents">The accepted archive.</param>
    /// <returns>True for a package with a macro part.</returns>
    private static bool HasMacroPart(ArchiveContents contents)
    {
        foreach (var entry in contents.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var name = entry.Name;
            var separator = name.LastIndexOf('/');
            var fileName = separator < 0 ? name : name[(separator + 1)..];

            if (string.Equals(fileName, MacroPartFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the content types declare a macro-enabled workbook or a VBA project.</summary>
    /// <param name="contentTypes">The parsed content-types part.</param>
    /// <returns>True for a macro-enabled package.</returns>
    private static bool DeclaresMacro(ParsedXmlReport contentTypes)
    {
        using var reader = contentTypes.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "Override", StringComparison.Ordinal))
            {
                continue;
            }

            var contentType = reader.GetAttribute("ContentType");

            if (contentType is not null
                && (contentType.Contains("macroenabled", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("vbaproject", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the first worksheet: the first <c>sheet</c> element's relationship id in the workbook, the matching
    /// relationship's target, and the target normalised against <c>xl/</c> — a target that escapes <c>xl/</c>, an
    /// absolute one or a non-worksheet relationship is refused.
    /// </summary>
    /// <param name="contents">The accepted archive.</param>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <param name="worksheet">The selected worksheet part's bytes.</param>
    /// <param name="issue">The refusal when the method returns false.</param>
    /// <returns>True when the worksheet part was resolved and is one the walk selected.</returns>
    private static bool TryResolveWorksheet(
        ArchiveContents contents,
        IParsing parsing,
        CancellationToken cancellationToken,
        out ReadOnlyMemory<byte> worksheet,
        [NotNullWhen(false)] out ImportIssue? issue)
    {
        worksheet = default;

        if (!contents.TryGetEntry(XlsxFormat.WorkbookPart, out var workbookPart)
            || !contents.TryGetEntry(XlsxFormat.WorkbookRelsPart, out var relationshipsPart))
        {
            issue = FileIssue(UnsupportedFormat);

            return false;
        }

        if (!TryParsePart(parsing, workbookPart, cancellationToken, out var workbook, out issue)
            || !TryParsePart(parsing, relationshipsPart, cancellationToken, out var relationships, out issue))
        {
            return false;
        }

        var relationshipId = FirstSheetRelationshipId(workbook);

        if (relationshipId is null)
        {
            issue = FileIssue(UnsupportedFormat);

            return false;
        }

        var target = WorksheetRelationshipTarget(relationships, relationshipId);

        if (target is null || !contents.TryGetEntry(target, out worksheet))
        {
            issue = FileIssue(UnsupportedFormat);

            return false;
        }

        issue = null;

        return true;
    }

    /// <summary>The relationship id of the first <c>sheet</c> element of the workbook part.</summary>
    /// <param name="workbook">The parsed workbook part.</param>
    /// <returns>The id, or null when the workbook declares no sheet.</returns>
    private static string? FirstSheetRelationshipId(ParsedXmlReport workbook)
    {
        using var reader = workbook.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "sheet", StringComparison.Ordinal))
            {
                continue;
            }

            var id = reader.GetAttribute("id", XlsxFormat.RelationshipsNamespace) ?? reader.GetAttribute("id");

            return string.IsNullOrEmpty(id) ? null : id;
        }

        return null;
    }

    /// <summary>The normalised target of the worksheet relationship <paramref name="relationshipId"/> names.</summary>
    /// <param name="relationships">The parsed workbook relationships part.</param>
    /// <param name="relationshipId">The id the workbook's first sheet element carries.</param>
    /// <returns>The part name inside the package, or null when the relationship is absent, not a worksheet or escapes <c>xl/</c>.</returns>
    private static string? WorksheetRelationshipTarget(ParsedXmlReport relationships, string relationshipId)
    {
        using var reader = relationships.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "Relationship", StringComparison.Ordinal)
                || !string.Equals(reader.GetAttribute("Id"), relationshipId, StringComparison.Ordinal))
            {
                continue;
            }

            var type = reader.GetAttribute("Type");

            if (type is not null && !type.EndsWith("/worksheet", StringComparison.Ordinal))
            {
                return null;
            }

            return ResolveTarget(reader.GetAttribute("Target"));
        }

        return null;
    }

    /// <summary>
    /// Normalises a relationship target against the workbook part's own directory (<c>xl/</c>): an absolute target, a
    /// scheme, a backslash, a fragment or a query is refused, as is one whose <c>..</c> segments climb out of
    /// <c>xl/</c>. Everything else resolves to a normalised part name.
    /// </summary>
    /// <param name="target">The <c>Target</c> attribute as the relationships part spells it.</param>
    /// <returns>The normalised part name, or null when the target escapes <c>xl/</c> or is not a part name.</returns>
    private static string? ResolveTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || target[0] == '/'
            || target.Contains(':', StringComparison.Ordinal)
            || target.Contains('\\', StringComparison.Ordinal)
            || target.Contains('?', StringComparison.Ordinal)
            || target.Contains('#', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = new List<string>(4) { "xl" };

        foreach (var segment in target.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (segments.Count <= 1)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count < 3 ? null : string.Join('/', segments);
    }

    /// <summary>Parses the shared-string table, when the package carries one.</summary>
    /// <param name="contents">The accepted archive.</param>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <param name="issue">The refusal when the table is over its cap or unreadable.</param>
    /// <returns>The table, or an empty one when the package inlines its strings.</returns>
    private static List<string> ReadSharedStrings(
        ArchiveContents contents,
        IParsing parsing,
        CancellationToken cancellationToken,
        out ImportIssue? issue)
    {
        issue = null;

        if (!contents.TryGetEntry(XlsxFormat.SharedStringsPart, out var part) || part.IsEmpty)
        {
            return [];
        }

        if (!TryParsePart(parsing, part, cancellationToken, out var report, out issue))
        {
            return [];
        }

        var strings = new List<string>();
        var characters = 0L;
        using var reader = report.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "si", StringComparison.Ordinal))
            {
                continue;
            }

            if (strings.Count >= XlsxFormat.MaxSharedStrings)
            {
                issue = FileIssue(UnsupportedFormat);

                return [];
            }

            // Every string is capped at one character past a cell, because a longer one can never fill a cell: the
            // cap keeps one hostile <t> of up to MaxPartBytes from becoming a 32 M-character string.
            var text = ReadStringItemText(reader, TextCharLimit);
            characters += text.Length;

            if (characters > MaxSharedStringChars)
            {
                issue = FileIssue(UnsupportedFormat);

                return [];
            }

            strings.Add(text);
        }

        return strings;
    }
}
