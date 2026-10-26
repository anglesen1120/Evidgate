using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// XLSX is the third carrier of the one flat import table (13.1.1 AC13/AC14/AC16, T5.2): a SpreadsheetML workbook
/// whose first worksheet holds exactly the columns of <see cref="ColumnNames"/>, read under the 06.3.1 archive
/// profile and written as a SpreadsheetML package this module's own reader accepts.
/// </summary>
/// <remarks>
/// <para>
/// Reading never opens the zip itself: <c>IParsing.ReadArchiveEntries</c> walks the whole archive under this
/// format's own caps (<see cref="ImportLimits.MaxXlsxEntries"/> entries,
/// <see cref="ImportLimits.MaxXlsxExpandedBytes"/> expanded bytes, <see cref="ImportLimits.MaxXlsxCompressionRatio"/>:1
/// per entry, AC16) and hands back only the parts a workbook needs, so an entry flood, an expanded-size bomb, a
/// compression bomb, a traversal name, a DTD or a macro part end as one file-level refusal with a named reason and
/// never as an exception (00 §5.7 S3/S5). Every XML part is then parsed only through <c>IParsing.ParseXmlReport</c>,
/// which streams it with a <c>DtdProcessing.Prohibit</c> reader; no part is materialised as a DOM.
/// </para>
/// <para>
/// The writer builds the package parts by hand (coordinator decision Q1: no third-party workbook library) in a
/// fixed entry order with a fixed 1980-01-01 timestamp, so exporting an unchanged tree twice is byte-identical
/// (AC14). Text cells are inline strings, NFC, formula-neutralised through <see cref="ImportText"/>, with XML-1.0
/// control characters and unpaired surrogates carried as <c>_xHHHH_</c> escapes and <c>xml:space="preserve"/> so
/// leading and trailing spaces survive; the reader reverses exactly that, so a value round-trips unchanged.
/// </para>
/// <para>
/// Every failure is an <see cref="ImportIssue"/> carrying a fixed pointer and a reason — never a cell value, a step
/// text, a key or a catalog name (S3). The reader refuses, it never repairs.
/// </para>
/// </remarks>
internal static class XlsxFormat
{
    /// <summary>The SpreadsheetML main namespace every workbook part uses.</summary>
    internal const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>The officeDocument relationship namespace (the <c>r:id</c> of a workbook sheet element).</summary>
    internal const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>The OPC relationships namespace every <c>*.rels</c> part uses.</summary>
    internal const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The OPC content-types namespace.</summary>
    internal const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>The content type of a workbook's main part.</summary>
    internal const string WorkbookContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";

    /// <summary>The content type of a worksheet part.</summary>
    internal const string WorksheetContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

    /// <summary>The relationship type of a worksheet part.</summary>
    internal const string WorksheetRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";

    /// <summary>The relationship type of the package's main document part.</summary>
    internal const string OfficeDocumentRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    /// <summary>The package's content-types part.</summary>
    internal const string ContentTypesPart = "[Content_Types].xml";

    /// <summary>The package's root relationships part.</summary>
    internal const string PackageRelsPart = "_rels/.rels";

    /// <summary>The workbook's main part.</summary>
    internal const string WorkbookPart = "xl/workbook.xml";

    /// <summary>The workbook relationships part (<c>xl/_rels/workbook.xml.rels</c>).</summary>
    internal const string WorkbookRelsPart = "xl/_rels/workbook.xml.rels";

    /// <summary>The shared-string table part; optional, every writer may inline its strings instead.</summary>
    internal const string SharedStringsPart = "xl/sharedStrings.xml";

    /// <summary>The only worksheet directory this format accepts, with its trailing separator.</summary>
    internal const string WorksheetDirectory = "xl/worksheets/";

    /// <summary>The one worksheet the writer emits.</summary>
    internal const string WorksheetPart = "xl/worksheets/sheet1.xml";

    /// <summary>The sheet name the writer emits.</summary>
    internal const string SheetName = "Authoring";

    /// <summary>
    /// The most bytes one selected part may expand to, checked by the profile before it allocates: 32 MiB, half the
    /// AC16 expanded cap, so a single part can never take the whole budget (a workbook part larger than this is not
    /// a workbook this format reads).
    /// </summary>
    internal const long MaxPartBytes = 32L * 1024 * 1024;

    /// <summary>
    /// The most shared strings one workbook may declare. The table is only ever addressed by the 13 + 5 000 cells
    /// this format reads, so a larger one is a file this format does not accept.
    /// </summary>
    internal const int MaxSharedStrings = 65_536;

    /// <summary>
    /// The flat table's columns, in contract order and spelling (ASCII snake_case): the one list the header row, the
    /// archive reader and the writer all use (<see cref="ImportSchema.ColumnNames"/>), so a column can never shift
    /// between the header a file carries and the cell that fills it.
    /// </summary>
    internal static string[] ColumnNames => ImportSchema.ColumnNames;

    /// <summary>The number of columns of the flat table.</summary>
    internal static int ColumnCount => ImportSchema.ColumnNames.Length;

    /// <summary>The file-level pointer every archive, header and macro refusal carries (S3: no name, no path).</summary>
    internal const string FilePath = "/file";

    /// <summary>
    /// Reads one XLSX body into the shared row table (13.1.1 AC13/AC14/AC16).
    /// </summary>
    /// <param name="body">The already-bounded request body (at most <see cref="ImportLimits.MaxBodyBytes"/> bytes).</param>
    /// <param name="parsing">The 06.3.1 parser profile the archive walk and every XML part go through.</param>
    /// <param name="cancellationToken">Cancels the read; the profile adds its own parse timeout.</param>
    /// <returns>The rows, the refusals, or the 413 verdict for a file over the row cap.</returns>
    internal static ImportParse Read(byte[] body, IParsing parsing, CancellationToken cancellationToken) =>
        XlsxReader.Read(body, parsing, cancellationToken);

    /// <summary>
    /// Writes the row table as a SpreadsheetML package (13.1.1 AC14). Two calls over the same rows produce
    /// byte-identical files: the parts, their order, their stamp and their XML are all fixed.
    /// </summary>
    /// <param name="rows">The rows to write, in export order.</param>
    /// <param name="output">The destination; it may be non-seekable (an HTTP response body) and stays open.</param>
    /// <param name="cancellationToken">Checked between rows.</param>
    /// <returns>A task that completes when the package has been written.</returns>
    internal static Task WriteAsync(
        IReadOnlyList<ImportRow> rows,
        Stream output,
        CancellationToken cancellationToken) =>
        XlsxWriter.WriteAsync(rows, output, cancellationToken);

    /// <summary>
    /// The predicate the archive walk selects parts with: the content types, the package and workbook
    /// relationships, the workbook, the shared strings and the worksheets directly under
    /// <see cref="WorksheetDirectory"/>. Matching is case-insensitive because OPC part names are.
    /// </summary>
    /// <param name="normalizedName">The entry's normalized name as the profile reports it.</param>
    /// <returns>True when the part is one this format reads.</returns>
    internal static bool IsSelectedPart(string normalizedName)
    {
        if (string.IsNullOrEmpty(normalizedName))
        {
            return false;
        }

        if (string.Equals(normalizedName, ContentTypesPart, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedName, PackageRelsPart, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedName, WorkbookPart, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedName, WorkbookRelsPart, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedName, SharedStringsPart, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!normalizedName.StartsWith(WorksheetDirectory, StringComparison.OrdinalIgnoreCase)
            || !normalizedName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = normalizedName.AsSpan(WorksheetDirectory.Length, normalizedName.Length - WorksheetDirectory.Length - 4);

        return !remainder.IsEmpty && !remainder.Contains('/');
    }

    /// <summary>
    /// Escapes a text value for a SpreadsheetML cell: a control character XML 1.0 cannot carry (anything below
    /// U+0020 except TAB, LF and CR) and an unpaired surrogate become the <c>_xHHHH_</c> form Excel itself uses, and
    /// a literal sequence that already looks like one gets its leading underscore escaped (<c>_x005F_</c>) so the
    /// value round-trips instead of turning into an escape. <see cref="Unescape"/> reverses this exactly.
    /// </summary>
    /// <param name="value">The NFC text to carry.</param>
    /// <returns>The cell text to write.</returns>
    internal static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!NeedsEscape(value))
        {
            return value;
        }

        var escaped = new System.Text.StringBuilder(value.Length + 8);

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];

            if (current == '_' && StartsEscapeSequence(value, index))
            {
                escaped.Append("_x005F_");
                continue;
            }

            if (char.IsHighSurrogate(current) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                escaped.Append(current).Append(value[index + 1]);
                index++;
                continue;
            }

            if (char.IsSurrogate(current)
                || current is '\uFFFE' or '\uFFFF'
                || (current < ' ' && current is not '\t' and not '\n' and not '\r'))
            {
                escaped.Append("_x")
                    .Append(((int)current).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))
                    .Append('_');
                continue;
            }

            escaped.Append(current);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Reverses <see cref="Escape"/>: every <c>_xHHHH_</c> token becomes its UTF-16 code unit in one pass, so the
    /// <c>_x005F_x0041_</c> form yields the literal <c>_x0041_</c> text and never a second expansion.
    /// </summary>
    /// <param name="value">The cell text as the workbook holds it.</param>
    /// <returns>The text the value had before escaping.</returns>
    internal static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var first = value.IndexOf("_x", StringComparison.OrdinalIgnoreCase);

        if (first < 0)
        {
            return value;
        }

        var unescaped = new System.Text.StringBuilder(value.Length);

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '_' && TryReadEscape(value, index, out var decoded))
            {
                unescaped.Append(decoded);
                index += 6;
                continue;
            }

            unescaped.Append(value[index]);
        }

        return unescaped.ToString();
    }

    /// <summary>True when the value holds a character or a literal escape sequence that must be escaped.</summary>
    /// <param name="value">The text to inspect.</param>
    /// <returns>True when <see cref="Escape"/> changes the value.</returns>
    private static bool NeedsEscape(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];

            if (current == '_' && StartsEscapeSequence(value, index))
            {
                return true;
            }

            if (char.IsSurrogate(current))
            {
                if (char.IsHighSurrogate(current) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    index++;
                    continue;
                }

                return true;
            }

            if (current is '\uFFFE' or '\uFFFF')
            {
                return true;
            }

            if (current < ' ' && current is not '\t' and not '\n' and not '\r')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <c>value[index..]</c> starts a seven-character <c>_xHHHH_</c> token.</summary>
    /// <param name="value">The text to inspect.</param>
    /// <param name="index">The index of the candidate underscore.</param>
    /// <returns>True for a well-formed token.</returns>
    private static bool StartsEscapeSequence(string value, int index) =>
        index + 6 < value.Length
        && value[index] == '_'
        && (value[index + 1] is 'x' or 'X')
        && value[index + 6] == '_'
        && IsHex(value[index + 2]) && IsHex(value[index + 3]) && IsHex(value[index + 4]) && IsHex(value[index + 5]);

    /// <summary>Reads the escape token at <paramref name="index"/>, which the caller has shaped like one.</summary>
    /// <param name="value">The text to read.</param>
    /// <param name="index">The index of the token's underscore.</param>
    /// <param name="decoded">The code unit the token spells.</param>
    /// <returns>True when the token was well formed.</returns>
    private static bool TryReadEscape(string value, int index, out char decoded)
    {
        decoded = default;

        if (!StartsEscapeSequence(value, index)
            || !ushort.TryParse(
                value.AsSpan(index + 2, 4),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var codeUnit))
        {
            return false;
        }

        decoded = (char)codeUnit;

        return true;
    }

    /// <summary>True for one hexadecimal digit.</summary>
    /// <param name="candidate">The character to test.</param>
    /// <returns>True for <c>0-9</c>, <c>a-f</c> or <c>A-F</c>.</returns>
    private static bool IsHex(char candidate) =>
        char.IsAsciiHexDigit(candidate);
}
