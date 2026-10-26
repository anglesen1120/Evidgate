using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// Writes the flat row table as a SpreadsheetML package (13.1.1 AC14, T5.2): the five parts a workbook needs —
/// the content types, the package relationships, the workbook, its relationships and one worksheet of inline
/// strings — in a fixed entry order with a fixed timestamp, so two exports of the same rows are byte-identical.
/// </summary>
/// <remarks>
/// <para>
/// The reader is this module's own (<see cref="XlsxReader"/>), so the package is deliberately the smallest one both
/// Excel and that reader accept: no styles, no shared strings, no theme, no document properties — a part this
/// format does not read is a part it does not write (coordinator decision Q1).
/// </para>
/// <para>
/// Text is composed (NFC) and formula-neutralised through <see cref="ImportText"/>, then carried as an inline
/// string with <c>xml:space="preserve"/>; XML-1.0 control characters and unpaired surrogates become <c>_xHHHH_</c>
/// escapes via <see cref="XlsxFormat.Escape"/>. An empty cell is written as no cell at all, which round-trips as
/// an absent value (the table treats empty and absent as the same thing).
/// </para>
/// </remarks>
internal static class XlsxWriter
{
    /// <summary>
    /// The stamp every entry carries: the earliest a zip can express. A fixed stamp is what makes two exports of
    /// one tree byte-identical — a clock reading would leak the export time into the file (AC14).
    /// </summary>
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The content type of the package's relationship parts.</summary>
    private const string RelationshipsContentType = "application/vnd.openxmlformats-package.relationships+xml";

    /// <summary>The content type of a plain XML part.</summary>
    private const string XmlContentType = "application/xml";

    /// <summary>The package's document-properties-free shape: the only extension defaults it declares.</summary>
    private const string RelsExtension = "rels";

    /// <summary>The second extension default the content types declare.</summary>
    private const string XmlExtension = "xml";

    /// <summary>The prefix the workbook's sheet element uses for its officeDocument relationship.</summary>
    private const string RelationshipsPrefix = "r";

    /// <summary>The relationship id of the one worksheet, in both the workbook part and its rels.</summary>
    private const string SheetRelationshipId = "rId1";

    /// <summary>The relationship id of the workbook in the package rels.</summary>
    private const string WorkbookRelationshipId = "rId1";

    /// <summary>The one column whose cell is a number rather than text (<c>position</c>).</summary>
    private const string PositionColumnName = "position";

    /// <summary>Writes the whole package to <paramref name="output"/>.</summary>
    /// <param name="rows">The rows to write, in export order (row 2 of the sheet is the first row).</param>
    /// <param name="output">The destination; it may be non-seekable and stays open.</param>
    /// <param name="cancellationToken">Checked between rows.</param>
    /// <returns>A completed task; the parts are written synchronously into the archive's own streams.</returns>
    internal static async Task WriteAsync(
        IReadOnlyList<ImportRow> rows,
        Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(output);

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        await WritePartAsync(archive, XlsxFormat.ContentTypesPart, WriteContentTypes, cancellationToken)
            .ConfigureAwait(false);
        await WritePartAsync(archive, XlsxFormat.PackageRelsPart, WritePackageRelationships, cancellationToken)
            .ConfigureAwait(false);
        await WritePartAsync(archive, XlsxFormat.WorkbookPart, WriteWorkbook, cancellationToken)
            .ConfigureAwait(false);
        await WritePartAsync(archive, XlsxFormat.WorkbookRelsPart, WriteWorkbookRelationships, cancellationToken)
            .ConfigureAwait(false);

        var worksheet = CreateEntry(archive, XlsxFormat.WorksheetPart);

        await using (var stream = await worksheet.OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            WriteWorksheet(stream, rows, cancellationToken);
        }
    }

    /// <summary>Creates one entry with the fixed stamp every entry of the package carries.</summary>
    /// <param name="archive">The archive being written.</param>
    /// <param name="name">The entry's part name.</param>
    /// <returns>The created entry.</returns>
    private static ZipArchiveEntry CreateEntry(ZipArchive archive, string name)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTimestamp;

        return entry;
    }

    /// <summary>Writes one part as a small XML document with a fixed writer profile.</summary>
    /// <param name="archive">The archive being written.</param>
    /// <param name="name">The part name.</param>
    /// <param name="body">Writes the part's elements into the given writer.</param>
    /// <param name="cancellationToken">Cancels opening the entry.</param>
    /// <returns>A task that completes when the part is written and its entry closed.</returns>
    private static async Task WritePartAsync(
        ZipArchive archive,
        string name,
        Action<XmlWriter> body,
        CancellationToken cancellationToken)
    {
        var entry = CreateEntry(archive, name);

        await using (var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            WriteDocument(stream, body);
        }
    }

    /// <summary>Writes one part's XML into an entry stream; synchronous on purpose (an entry stream's synchronous
    /// writes are what the archive buffers, and every write call would otherwise be a task of its own).</summary>
    /// <param name="target">The entry's stream.</param>
    /// <param name="body">Writes the part's elements into the given writer.</param>
    private static void WriteDocument(Stream target, Action<XmlWriter> body)
    {
        using var writer = XmlWriter.Create(target, Settings());

        writer.WriteStartDocument();
        body(writer);
        writer.WriteEndDocument();
        writer.Flush();
    }

    /// <summary>The one writer profile every part uses: UTF-8 without a BOM, no indentation, no extra whitespace.</summary>
    /// <returns>The settings.</returns>
    private static XmlWriterSettings Settings() => new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        CloseOutput = false,
    };

    /// <summary>Writes <c>[Content_Types].xml</c>: the two extension defaults and one override per part.</summary>
    /// <param name="writer">The part's writer.</param>
    private static void WriteContentTypes(XmlWriter writer)
    {
        writer.WriteStartElement("Types", XlsxFormat.ContentTypesNamespace);

        WriteDefault(writer, RelsExtension, RelationshipsContentType);
        WriteDefault(writer, XmlExtension, XmlContentType);
        WriteOverride(writer, "/" + XlsxFormat.WorkbookPart, XlsxFormat.WorkbookContentType);
        WriteOverride(writer, "/" + XlsxFormat.WorksheetPart, XlsxFormat.WorksheetContentType);

        writer.WriteEndElement();
    }

    /// <summary>Writes one <c>Default</c> element of the content types.</summary>
    /// <param name="writer">The part's writer.</param>
    /// <param name="extension">The file extension the default covers.</param>
    /// <param name="contentType">Its content type.</param>
    private static void WriteDefault(XmlWriter writer, string extension, string contentType)
    {
        writer.WriteStartElement("Default", XlsxFormat.ContentTypesNamespace);
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    /// <summary>Writes one <c>Override</c> element of the content types.</summary>
    /// <param name="writer">The part's writer.</param>
    /// <param name="partName">The part's absolute name inside the package.</param>
    /// <param name="contentType">Its content type.</param>
    private static void WriteOverride(XmlWriter writer, string partName, string contentType)
    {
        writer.WriteStartElement("Override", XlsxFormat.ContentTypesNamespace);
        writer.WriteAttributeString("PartName", partName);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    /// <summary>Writes <c>_rels/.rels</c>: the one officeDocument relationship to the workbook part.</summary>
    /// <param name="writer">The part's writer.</param>
    private static void WritePackageRelationships(XmlWriter writer)
    {
        writer.WriteStartElement("Relationships", XlsxFormat.PackageRelationshipsNamespace);
        WriteRelationship(
            writer,
            WorkbookRelationshipId,
            XlsxFormat.OfficeDocumentRelationshipType,
            XlsxFormat.WorkbookPart);
        writer.WriteEndElement();
    }

    /// <summary>Writes one <c>Relationship</c> element.</summary>
    /// <param name="writer">The part's writer.</param>
    /// <param name="id">The relationship id a part references it by.</param>
    /// <param name="type">The relationship type.</param>
    /// <param name="target">The target, relative to the part that owns the rels.</param>
    private static void WriteRelationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", XlsxFormat.PackageRelationshipsNamespace);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    /// <summary>Writes <c>xl/workbook.xml</c>: a workbook holding the one named sheet.</summary>
    /// <param name="writer">The part's writer.</param>
    private static void WriteWorkbook(XmlWriter writer)
    {
        writer.WriteStartElement("workbook", XlsxFormat.MainNamespace);
        writer.WriteAttributeString(
            "xmlns",
            RelationshipsPrefix,
            null,
            XlsxFormat.RelationshipsNamespace);

        writer.WriteStartElement("sheets", XlsxFormat.MainNamespace);
        writer.WriteStartElement("sheet", XlsxFormat.MainNamespace);
        writer.WriteAttributeString("name", XlsxFormat.SheetName);
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString(RelationshipsPrefix, "id", XlsxFormat.RelationshipsNamespace, SheetRelationshipId);
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>The worksheet part's target relative to the workbook part's own directory (<c>xl/</c>).</summary>
    private const string WorksheetTarget = "worksheets/sheet1.xml";

    /// <summary>Writes <c>xl/_rels/workbook.xml.rels</c>: the worksheet relationship the sheet element names.</summary>
    /// <param name="writer">The part's writer.</param>
    private static void WriteWorkbookRelationships(XmlWriter writer)
    {
        writer.WriteStartElement("Relationships", XlsxFormat.PackageRelationshipsNamespace);
        WriteRelationship(
            writer,
            SheetRelationshipId,
            XlsxFormat.WorksheetRelationshipType,
            WorksheetTarget);
        writer.WriteEndElement();
    }

    /// <summary>Writes the worksheet: the header row, then one sheet row per import row, in order.</summary>
    /// <param name="target">The worksheet part's stream.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="cancellationToken">Checked between rows.</param>
    private static void WriteWorksheet(Stream target, IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        using var writer = XmlWriter.Create(target, Settings());

        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", XlsxFormat.MainNamespace);
        writer.WriteStartElement("sheetData", XlsxFormat.MainNamespace);

        WriteHeaderRow(writer);

        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteDataRow(writer, rows[index], index + 2);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
    }

    /// <summary>Writes row 1: the column names of the flat table, in contract order.</summary>
    /// <param name="writer">The sheet's writer.</param>
    private static void WriteHeaderRow(XmlWriter writer)
    {
        WriteRowStart(writer, 1);

        var names = XlsxFormat.ColumnNames;

        for (var index = 0; index < names.Length; index++)
        {
            WriteTextCell(writer, Reference(index + 1, 1), names[index]);
        }

        writer.WriteEndElement();
    }

    /// <summary>Writes one data row: every present text column as an inline string and the position as a number.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="row">The row to write.</param>
    /// <param name="rowNumber">Its 1-based sheet row (the header is row 1).</param>
    private static void WriteDataRow(XmlWriter writer, ImportRow row, int rowNumber)
    {
        WriteRowStart(writer, rowNumber);

        var names = XlsxFormat.ColumnNames;

        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            var column = index + 1;

            if (string.Equals(name, PositionColumnName, StringComparison.Ordinal))
            {
                if (row.Position is { } position)
                {
                    WriteNumberCell(writer, Reference(column, rowNumber), position);
                }

                continue;
            }

            if (TextOf(row, name) is { Length: > 0 } text)
            {
                WriteTextCell(writer, Reference(column, rowNumber), text);
            }
        }

        writer.WriteEndElement();
    }

    /// <summary>Starts a <c>row</c> element with its explicit number.</summary>
    /// <param name="writer">The sheet's writer.</param>
    /// <param name="rowNumber">The 1-based sheet row.</param>
    private static void WriteRowStart(XmlWriter writer, int rowNumber)
    {
        writer.WriteStartElement("row", XlsxFormat.MainNamespace);
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes one inline-string cell: NFC text, formula-neutralised, escaped, whitespace preserved.</summary>
    /// <param name="writer">The sheet's writer.</param>
    /// <param name="reference">The A1-style cell reference.</param>
    /// <param name="value">The text the cell carries.</param>
    private static void WriteTextCell(XmlWriter writer, string reference, string value)
    {
        // Normalise first: the export writes NFC (AC14). A value this process cannot compose is impossible from the
        // write path, which refuses it; writing it unchanged keeps the export honest instead of repairing it.
        var normalised = ImportText.Normalise(value, out var invalid);
        var carried = XlsxFormat.Escape(
            ImportText.NeutraliseForSpreadsheet(invalid ? value : normalised ?? value));

        writer.WriteStartElement("c", XlsxFormat.MainNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is", XlsxFormat.MainNamespace);
        writer.WriteStartElement("t", XlsxFormat.MainNamespace);
        writer.WriteAttributeString("xml", "space", null, "preserve");
        writer.WriteString(carried);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>Writes one numeric cell (the position column): a plain <c>v</c> a reader can parse invariantly.</summary>
    /// <param name="writer">The sheet's writer.</param>
    /// <param name="reference">The A1-style cell reference.</param>
    /// <param name="value">The number the cell carries.</param>
    private static void WriteNumberCell(XmlWriter writer, string reference, int value)
    {
        writer.WriteStartElement("c", XlsxFormat.MainNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("t", "n");
        writer.WriteStartElement("v", XlsxFormat.MainNamespace);
        writer.WriteString(value.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>The text of one table column of <paramref name="row"/>, or null when the column does not apply.</summary>
    /// <param name="row">The row to read.</param>
    /// <param name="column">One name of <see cref="XlsxFormat.ColumnNames"/>.</param>
    /// <returns>The column's text, or null for a column the row's kind leaves empty.</returns>
    private static string? TextOf(ImportRow row, string column) => column switch
    {
        "kind" => row.Kind switch
        {
            ImportRowKind.Node => "node",
            ImportRowKind.Item => "item",
            _ => "case",
        },
        "key" => row.Key,
        "parent_key" => row.ParentKey,
        "item_key" => row.ItemKey,
        "node_kind" => row.NodeKind,
        "title" => row.Title,
        "description" => row.Description,
        "precondition" => row.Precondition,
        "steps" => row.StepsJson,
        "priority" => row.Priority,
        "state" => row.State,
        "code" => row.Code,
        _ => null,
    };

    /// <summary>The A1-style reference of one cell; the table never needs more than the 13 single-letter columns.</summary>
    /// <param name="column">The 1-based column (1..13).</param>
    /// <param name="rowNumber">The 1-based sheet row.</param>
    /// <returns>The reference, e.g. <c>M2</c>.</returns>
    private static string Reference(int column, int rowNumber) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(char)('A' + (column - 1))}{rowNumber}");
}
