using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T5.1, the CSV and JSON halves of the import/export table (AC13/AC14/AC16): both readers produce the same
/// <see cref="ImportRow"/> projection from the same tree, both writers are deterministic, and every bound the plan puts
/// on a file is refused with its own reason — no database, because a format reader only ever sees bytes.
/// </summary>
/// <remarks>
/// The projection equality is asserted over <see cref="ImportRow"/> records (write → read → equal), which is what makes
/// AC14's round trip a property of the code rather than of one sample: the multiline, quoted, comma-bearing, NFD and
/// formula-leading values of <see cref="SampleRows"/> all survive, and the only difference the reader may introduce is
/// NFC.
/// </remarks>
public sealed class AuthoringImportFormatUnitTests
{
    /// <summary>"Café" in NFD: the reader must compose it, which is why the round trip compares against the NFC copy.</summary>
    private const string NfdTitle = "Cafe\u0301 au lait";

    private const string NfcTitle = "Caf\u00e9 au lait";

    /// <summary>A compact steps document: the shape <c>authoring-import.v1</c> requires of a case row.</summary>
    private const string Steps = """{"steps":[{"no":1,"action":"Open the cart","expected":"The cart is shown"}]}""";

    private const string Header =
        "kind,key,parent_key,item_key,node_kind,title,description,precondition,steps,priority,state,code,position";

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // ------------------------------------------------------------------ round trips (AC14)

    [Fact]
    public async Task Csv_RoundTrip_ProjectionEqual()
    {
        var rows = SampleRows();

        var bytes = await WriteCsv(rows);

        // The export shape of AC16: a BOM a spreadsheet needs, and CRLF line ends.
        bytes.Take(3).ShouldBe(Bom);
        CsvText(bytes).ShouldContain("\r\n", Case.Sensitive);

        var parse = CsvFormat.Read(bytes);

        parse.IsSuccess.ShouldBeTrue(Describe(parse));

        // NFD in, NFC out: the single permitted difference between the input rows and the projection.
        NfdTitle.ShouldNotBe(NfcTitle);
        parse.Rows[0].Title.ShouldBe(NfcTitle);
        parse.Rows.ShouldBe(rows.Select(Nfc).ToArray());
    }

    [Fact]
    public async Task Json_RoundTrip_ProjectionEqual()
    {
        var rows = SampleRows();

        var bytes = await WriteJson(rows);

        // No BOM and no indentation: the document is the contract, not a document for a human to diff.
        bytes[0].ShouldBe((byte)'{');
        CsvText(bytes).ShouldStartWith("""{"format":"authoring-import","version":1,"rows":[""", Case.Sensitive);

        var parse = JsonFormat.Read(bytes);

        parse.IsSuccess.ShouldBeTrue(Describe(parse));
        parse.Rows.ShouldBe(rows.Select(Nfc).ToArray());
    }

    [Fact]
    public async Task Export_Twice_ByteIdentical()
    {
        // No timestamp, no hash ordering, no locale: the same tree written twice is the same file, so AC15's "unchanged
        // tree, unchanged file" holds for both formats.
        (await WriteCsv(SampleRows())).ShouldBe(await WriteCsv(SampleRows()));
        (await WriteJson(SampleRows())).ShouldBe(await WriteJson(SampleRows()));
    }

    // ------------------------------------------------------------------ CSV framing (AC14)

    [Fact]
    public async Task Csv_BomAndCrLfTolerated()
    {
        var expected = SampleRows().Select(Nfc).ToArray();
        var exported = await WriteCsv(SampleRows());

        // The exported file itself, a copy re-ended with LF, and a copy of that with a BOM: one projection.
        CsvFormat.Read(exported).Rows.ShouldBe(expected);

        var lf = Utf8(CsvText(exported).Replace("\r\n", "\n", StringComparison.Ordinal));
        CsvFormat.Read(lf).Rows.ShouldBe(expected);
        CsvFormat.Read([.. Bom, .. lf]).Rows.ShouldBe(expected);

        // The shipped corpus is the same tree in both formats: one projection, whichever file it came from
        // (fixtures/authoring/import, shape of fixtures/README.md). The steps member of a JSON file is compared
        // structurally because a document may carry the whitespace its author typed.
        var csv = CsvFormat.Read(await File.ReadAllBytesAsync(Fixture("csv-sample", "input.csv"), TestContext.Current.CancellationToken));
        var json = JsonFormat.Read(await File.ReadAllBytesAsync(Fixture("json-sample", "input.json"), TestContext.Current.CancellationToken));
        csv.IsSuccess.ShouldBeTrue(Describe(csv));
        json.IsSuccess.ShouldBeTrue(Describe(json));
        csv.Rows.Count.ShouldBe(5);
        csv.Rows.Select(Canonical).ShouldBe(json.Rows.Select(Canonical));
    }

    [Fact]
    public void Csv_HeaderMismatch_Refused()
    {
        // One refusal for the file, not one per row: a header the contract does not describe means every cell would be
        // guessed, so the reader names the header and stops.
        foreach (var header in new[]
        {
            // Swapped columns.
            "key,kind,parent_key,item_key,node_kind,title,description,precondition,steps,priority,state,code,position",
            // An extra column.
            Header + ",extra",
            // A duplicate column.
            "kind,kind,parent_key,item_key,node_kind,title,description,precondition,steps,priority,state,code,position",
            // A missing column.
            "kind,key,parent_key,item_key,node_kind,title,description,precondition,steps,priority,state,code",
            // The right columns in the wrong case.
            "kind,key,parent_key,item_key,node_kind,Title,description,precondition,steps,priority,state,code,position",
        })
        {
            var parse = CsvFormat.Read(Utf8(header + "\r\nnode,n1,,,module,Area,,,,,,,0\r\n"));

            parse.TooLarge.ShouldBeFalse();
            parse.Issues.ShouldBe([new ImportIssue(null, "/header", "unsupported_format")]);
        }

        // An empty body names the same reason rather than claiming a zero-row file is fine.
        CsvFormat.Read([]).Issues.ShouldBe([new ImportIssue(null, "/header", "unsupported_format")]);
    }

    [Fact]
    public void Csv_RowCapOver5000_Large()
    {
        // 5 000 rows is the cap the route and the schema both declare; 5 001 is 413 (AC16) and is detected while the
        // file is being walked, before the 5 001st row is materialised.
        var accepted = CsvFormat.Read(NodeRows(ImportLimits.MaxDataRows));
        accepted.TooLarge.ShouldBeFalse();
        accepted.IsSuccess.ShouldBeTrue(Describe(accepted));
        accepted.Rows.Count.ShouldBe(ImportLimits.MaxDataRows);
        accepted.Rows[^1].Position.ShouldBe(ImportLimits.MaxDataRows - 1);

        var refused = CsvFormat.Read(NodeRows(ImportLimits.MaxDataRows + 1));
        refused.TooLarge.ShouldBeTrue();
        refused.IsSuccess.ShouldBeFalse();
    }

    // ------------------------------------------------------------------ cell rules (AC13/AC16)

    [Fact]
    public void Csv_CellOver64KiB_Refused()
    {
        // The body cap is 5 MiB: without a per-cell cap one cell could ask for a 5 MiB string, so a cell over 64 KiB is
        // refused before it is decoded or allocated.
        var overCap = new string('a', ImportText.MaxCellBytes + 1);
        var refused = CsvFormat.Read(Utf8($"{Header}\r\n{string.Join(',', "node", "n1", string.Empty, string.Empty, "module", "T", overCap, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "0")}\r\n"));

        refused.TooLarge.ShouldBeFalse();
        refused.Issues.ShouldBe([new ImportIssue(1, "/description", "value_invalid")]);

        // The cap is per cell, not per record: a file whose cells are all under it is accepted however long the record
        // is — even when the whole body is larger than one cell's cap.
        var longDescription = new string('b', 4000);
        var builder = new StringBuilder();
        builder.Append(Header).Append("\r\n");
        for (var number = 0; number < 40; number++)
        {
            builder.Append("item,i")
                .Append(number.ToString(CultureInfo.InvariantCulture))
                .Append(",n1,,,")
                .Append("Pay with a card,")
                .Append(longDescription)
                .Append(",,,high,active,,")
                .Append(number.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n");
        }

        var accepted = CsvFormat.Read(Utf8(builder.ToString()));
        accepted.IsSuccess.ShouldBeTrue(Describe(accepted));
        accepted.Rows.Count.ShouldBe(40);
        accepted.Rows[0].Description.ShouldBe(longDescription);
    }

    [Fact]
    public void Csv_InvalidUtf8_Unsupported()
    {
        // A byte that is not part of a UTF-8 sequence makes the cell unreadable: the cell boundary is still known (the
        // reader walks bytes, not characters), so the refusal names the column rather than the whole file.
        var body = new List<byte>(Utf8(Header + "\r\nnode,n1,,,module,a"));
        body.Add(0xFF);
        body.AddRange(Utf8("b,,,,,,,0\r\n"));

        CsvFormat.Read([.. body]).Issues.ShouldBe([new ImportIssue(1, "/title", "unsupported_format")]);
    }

    [Fact]
    public void Text_NulAndLoneSurrogate_InvalidText()
    {
        // CSV: a literal NUL byte in a cell. It is valid UTF-8 and valid to every parser, so only this check refuses it.
        var nulCsv = new List<byte>(Utf8(Header + "\r\n" + CsvTitleOpen("a")));
        nulCsv.Add(0x00);
        nulCsv.AddRange(Utf8("b,,,,,,,0\r\n"));
        CsvFormat.Read([.. nulCsv]).Issues.ShouldBe([new ImportIssue(1, "/title", "invalid_text")]);

        // CSV: a lone surrogate encoded as the three bytes CESU-8 uses for it — not decodable as UTF-8, and refused as
        // unstorable text rather than as an unsupported format, because the cell boundary is still known.
        var surrogateCsv = new List<byte>(Utf8(Header + "\r\n" + CsvTitleOpen("a")));
        surrogateCsv.AddRange([0xED, 0xA0, 0x80]);
        surrogateCsv.AddRange(Utf8("b,,,,,,,0\r\n"));
        CsvFormat.Read([.. surrogateCsv]).Issues.ShouldBe([new ImportIssue(1, "/title", "invalid_text")]);

        // JSON: an escaped NUL and an escaped lone surrogate in a value, an escaped lone surrogate in a row member name,
        // and one at the root. None of them throws: an unreadable name is a refusal, never a 500 (OI-13-116).
        JsonFormat.Read(Utf8(Document("""{"kind":"node","key":"n1","node_kind":"module","title":"a\u0000b"}"""))).Issues
            .ShouldBe([new ImportIssue(1, "/title", "invalid_text")]);
        JsonFormat.Read(Utf8(Document("""{"kind":"node","key":"n1","node_kind":"module","title":"a\ud800b"}"""))).Issues
            .ShouldBe([new ImportIssue(1, "/title", "invalid_text")]);

        // The member name is never echoed: the pointer is the row's own fixed pointer.
        JsonFormat.Read(Utf8(Document("""{"kind":"node","key":"n1","node_kind":"module","\ud800":1}"""))).Issues
            .ShouldBe([new ImportIssue(1, "/row", "invalid_text")]);
        JsonFormat.Read(Utf8("""{"\ud800":1}""")).Issues.ShouldBe([new ImportIssue(null, "/file", "invalid_text")]);
    }

    // ------------------------------------------------------------------ JSON document rules (AC13)

    [Fact]
    public void Json_UnknownMember_Refused()
    {
        // The document is closed: a member a row does not declare is refused rather than ignored, so a typo cannot
        // silence a column. The pointer names the row, not the caller's member name (S3).
        var parse = JsonFormat.Read(Utf8(Document("""{"kind":"node","key":"n1","node_kind":"module","title":"Area","bogus":1}""")));

        parse.IsSuccess.ShouldBeFalse();
        parse.Issues.ShouldContain(new ImportIssue(1, "/row", "value_invalid"));
        parse.Issues.ShouldNotContain(issue => issue.Path.Contains("bogus", StringComparison.Ordinal));

        // The same rule at the document level, with an otherwise valid row so the refusal is the unknown member's.
        var document = JsonFormat.Read(Utf8(
            "{\"format\":\"authoring-import\",\"version\":1,\"rows\":[" + Row("n1") + "],\"extra\":1}"));
        document.IsSuccess.ShouldBeFalse();
        document.Issues.ShouldContain(new ImportIssue(null, "/file", "value_invalid"));
        document.Issues.ShouldNotContain(issue => issue.Path.Contains("extra", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_DuplicateMember_Refused()
    {
        // Last-one-wins is how a duplicate silently changes a file's meaning: both levels refuse instead.
        JsonFormat.Read(Utf8("""{"format":"authoring-import","version":1,"version":1,"rows":[]}""")).Issues
            .ShouldBe([new ImportIssue(null, "/file", "value_invalid")]);

        JsonFormat.Read(Utf8(Document("""{"kind":"node","key":"n1","node_kind":"module","title":"Area","title":"Other"}"""))).Issues
            .ShouldBe([new ImportIssue(1, "/row", "value_invalid")]);
    }

    [Fact]
    public void Text_Noncharacter_InvalidText()
    {
        // U+FFFE and U+FFFF are code points the process's normaliser refuses, so this module cannot store them composed
        // (AC14). Every carrier answers the same way — one invalid_text issue at the column — and none of them throws,
        // which is what a reader that called the normaliser unguarded would do.
        var noncharacter = "a\uFFFFb";

        var csvRow = string.Join(
            ',',
            "node",
            "n1",
            string.Empty,
            string.Empty,
            "module",
            noncharacter,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "0");

        CsvFormat.Read(Utf8($"{Header}\r\n{csvRow}\r\n")).Issues.ShouldBe([new ImportIssue(1, "/title", "invalid_text")]);

        // JSON: the escape is what a caller writes, and the scan refuses it before the document is parsed.
        JsonFormat.Read(Utf8(Document("""{"kind":"node","key":"n1","node_kind":"module","title":"a\uffffb"}"""))).Issues
            .ShouldBe([new ImportIssue(1, "/title", "invalid_text")]);

        // The same rule for a member name: no name is echoed, so the pointer is the row's own.
        JsonFormat.Read(Utf8(Document("""{"kind":"node","key":"n1","node_kind":"module","a\uffffb":1}"""))).Issues
            .ShouldBe([new ImportIssue(1, "/row", "invalid_text")]);
    }

    [Fact]
    public void CodeAndKey_TrailingLineFeed_Refused()
    {
        // The contract's patterns are anchored with an ECMA-262 `$`, which means end of input; the engine the registry
        // uses also matches before a final line feed, so the validator applies the same patterns with an exact end. A
        // key or code carrying one is a different string from the key or code it looks like, so it must not reach the
        // planner, where "n1" and "n1\n" would be two rows and a code would fail the database's own CHECK instead.
        JsonFormat.Read(Utf8("""{"format":"authoring-import","version":1,"rows":[{"kind":"node","key":"n1\n","node_kind":"module","title":"Area"}]}"""))
            .Issues.ShouldBe([new ImportIssue(1, "/key", "value_invalid")]);

        JsonFormat
            .Read(Utf8(
                """{"format":"authoring-import","version":1,"rows":[{"kind":"case","key":"c1","item_key":"i1","title":"T","priority":"high","steps":{"steps":[{"no":1,"action":"Pay","expected":"Refused"}]},"code":"SHOP-001\n"}]}"""))
            .Issues.ShouldBe([new ImportIssue(1, "/code", "value_invalid")]);

        // The CSV cell is the same value: a quoted cell holding the line feed is refused at the same column.
        var csvKey = string.Join(
            ',',
            "node",
            "\"n1\n\"",
            string.Empty,
            string.Empty,
            "module",
            "Area",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "0");

        CsvFormat.Read(Utf8($"{Header}\r\n{csvKey}\r\n")).Issues.ShouldBe([new ImportIssue(1, "/key", "value_invalid")]);
    }

    [Fact]
    public void Csv_RecordOverTheColumnCount_Refused()
    {
        // A record with more fields than the table has columns is refused as a record, and the collector stops at the
        // cap: a 5 MiB line of commas must not become five million field spans before it is refused (AC16).
        var commas = new string(',', ImportLimits.MaxBodyBytes - Header.Length - 4);
        var parse = CsvFormat.Read(Utf8($"{Header}\r\n{commas}\r\n"));

        parse.TooLarge.ShouldBeFalse();
        parse.Rows.ShouldBeEmpty();
        parse.Issues.ShouldBe([new ImportIssue(1, "/row", "unsupported_format")]);

        // One field past the table — a valid node row with a fourteenth cell — is the count mismatch every reader's
        // cell twin reports the same way: value_invalid at the row, the same reason the XLSX reader gives a cell past
        // the last column. Only a record the collector had to stop reading (fifteen or more fields) is unsupported.
        var fourteen = string.Join(
            ',',
            "node",
            "n1",
            string.Empty,
            string.Empty,
            "module",
            "T",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "0",
            "extra");

        CsvFormat.Read(Utf8($"{Header}\r\n{fourteen}\r\n")).Issues
            .ShouldBe([new ImportIssue(1, "/row", "value_invalid")]);
    }

    // ------------------------------------------------------------------ neutralisation (AC16)

    [Fact]
    public async Task Neutralise_RoundTrip_BothDirections()
    {
        // Every trigger is prefixed on export and stripped on import: the stored text is literal, never evaluated.
        foreach (var trigger in new[] { "=", "+", "-", "@", "\t", "\r", "\n" })
        {
            var value = trigger + "cmd";

            ImportText.NeutraliseForSpreadsheet(value).ShouldBe("'" + value);
            ImportText.UnneutraliseFromSpreadsheet("'" + value).ShouldBe(value);
        }

        // An apostrophe that is not followed by a trigger is genuine text in both directions.
        ImportText.NeutraliseForSpreadsheet("'hello").ShouldBe("'hello");
        ImportText.UnneutraliseFromSpreadsheet("'hello").ShouldBe("'hello");
        ImportText.UnneutraliseFromSpreadsheet("''hello").ShouldBe("''hello");

        // A value that itself starts with apostrophes and a trigger grows the run by one on export, so the reader's
        // one-strip rule returns it unchanged rather than eating an apostrophe — for any length of run.
        ImportText.NeutraliseForSpreadsheet("'=cmd").ShouldBe("''=cmd");
        ImportText.UnneutraliseFromSpreadsheet("''=cmd").ShouldBe("'=cmd");
        ImportText.NeutraliseForSpreadsheet("''=cmd").ShouldBe("\u0027\u0027\u0027=cmd");
        ImportText.UnneutraliseFromSpreadsheet("\u0027\u0027\u0027=cmd").ShouldBe("''=cmd");
        ImportText.NeutraliseForSpreadsheet("''hello").ShouldBe("''hello");

        // Ordinary text and a trigger that is not in the first position are never touched.
        ImportText.NeutraliseForSpreadsheet("a=b").ShouldBe("a=b");
        ImportText.UnneutraliseFromSpreadsheet("a=b").ShouldBe("a=b");
        ImportText.NeutraliseForSpreadsheet(string.Empty).ShouldBe(string.Empty);

        // And the same through a CSV body: a stored "=cmd" is written neutralised and read back as "=cmd".
        var parse = CsvFormat.Read(await WriteCsv(
        [
            Node(1, "n1", "=cmd"),
            Node(2, "n2", "'=cmd"),
            Node(3, "n3", "'hello"),
        ]));

        parse.IsSuccess.ShouldBeTrue(Describe(parse));
        parse.Rows.Select(row => row.Title).ShouldBe(["=cmd", "'=cmd", "'hello"]);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The reference tree: two nodes, an item with a multiline quoted description, and two cases — one with a
    /// formula-leading title, one with every optional member absent.
    /// </summary>
    /// <returns>The rows, with 1-based indexes and NFC text apart from the NFD module title.</returns>
    private static IReadOnlyList<ImportRow> SampleRows() =>
    [
        new(1, ImportRowKind.Node, "n1", null, null, "module", NfdTitle, null, null, null, null, null, null, 0),
        new(2, ImportRowKind.Node, "n2", "n1", null, "feature", "Checkout", null, null, null, null, null, null, 0),
        new(
            3,
            ImportRowKind.Item,
            "i1",
            "n2",
            null,
            null,
            "Pay with a card",
            "The customer types a card, then submits.\nA second line \"quoted\", with a comma.",
            null,
            null,
            "high",
            "active",
            null,
            0),
        new(
            4,
            ImportRowKind.Case,
            "c1",
            null,
            "i1",
            null,
            "=Declined card",
            null,
            "The cart holds one item.",
            Steps,
            "critical",
            "draft",
            "SHOP-001",
            0),
        new(5, ImportRowKind.Case, "c2", null, "i1", null, "Empty optional members", null, null, Steps, "low", null, null, null),
    ];

    /// <summary>The NFC copy of a row: what a reader may legitimately change about it.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The row with every text member composed to NFC.</returns>
    private static ImportRow Nfc(ImportRow row) => row with
    {
        Title = row.Title?.Normalize(NormalizationForm.FormC),
        Description = row.Description?.Normalize(NormalizationForm.FormC),
        Precondition = row.Precondition?.Normalize(NormalizationForm.FormC),
    };

    /// <summary>One module row at the root, positioned by its index.</summary>
    /// <param name="index">The 1-based row index.</param>
    /// <param name="key">The row key.</param>
    /// <param name="title">The module title.</param>
    /// <returns>The row.</returns>
    private static ImportRow Node(int index, string key, string title) =>
        new(index, ImportRowKind.Node, key, null, null, "module", title, null, null, null, null, null, null, index - 1);

    /// <summary>A CSV body holding <paramref name="count"/> minimal node rows, one position per row.</summary>
    /// <param name="count">The number of data rows.</param>
    /// <returns>The UTF-8 body.</returns>
    private static byte[] NodeRows(int count)
    {
        var builder = new StringBuilder(count * 32);
        builder.Append(Header).Append("\r\n");
        for (var number = 1; number <= count; number++)
        {
            builder.Append(string.Join(
                ',',
                "node",
                "n" + number.ToString(CultureInfo.InvariantCulture),
                string.Empty,
                string.Empty,
                "module",
                "Area",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                (number - 1).ToString(CultureInfo.InvariantCulture))).Append("\r\n");
        }

        return Utf8(builder.ToString());
    }

    /// <summary>One valid node row as JSON object text.</summary>
    /// <param name="key">The row key.</param>
    /// <returns>The row object.</returns>
    private static string Row(string key) => $$"""{"kind":"node","key":"{{key}}","node_kind":"module","title":"Area"}""";

    /// <summary>A whole import document holding one row object.</summary>
    /// <param name="row">The row as a JSON object.</param>
    /// <returns>The document text.</returns>
    private static string Document(string row) =>
        $$"""{"format":"authoring-import","version":1,"rows":[{{row}}]}""";

    /// <summary>The canonical projection of a row for a cross-format comparison: its steps member compared structurally.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The row with a whitespace-independent steps text.</returns>
    private static ImportRow Canonical(ImportRow row) => row with
    {
        StepsJson = row.StepsJson is { } steps ? JsonNode.Parse(steps)?.ToJsonString() : null,
    };

    /// <summary>Writes rows as a CSV body.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The body.</returns>
    private static async Task<byte[]> WriteCsv(IReadOnlyList<ImportRow> rows)
    {
        using var stream = new MemoryStream();
        await CsvFormat.WriteAsync(rows, stream);
        return stream.ToArray();
    }

    /// <summary>Writes rows as a JSON document.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The body.</returns>
    private static async Task<byte[]> WriteJson(IReadOnlyList<ImportRow> rows)
    {
        using var stream = new MemoryStream();
        await JsonFormat.WriteAsync(rows, stream);
        return stream.ToArray();
    }

    /// <summary>A UTF-8 body without a BOM.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Utf8(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

    /// <summary>
    /// A data row's first six cells — through the start of its <c>title</c> — so a test can inject raw bytes into the
    /// title cell and close the record with <c>b,,,,,,,0</c> (the six remaining columns and the position).
    /// </summary>
    /// <param name="start">The title text before the injected bytes.</param>
    /// <returns>The record prefix, without a terminator.</returns>
    private static string CsvTitleOpen(string start) =>
        string.Join(',', "node", "n1", string.Empty, string.Empty, "module", start);

    /// <summary>A body read as text (a BOM prefix is dropped when present).</summary>
    /// <param name="bytes">The body.</param>
    /// <returns>The text.</returns>
    private static string CsvText(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Bom) ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);

    /// <summary>A file of the shipped corpus (<c>fixtures/authoring/import</c>).</summary>
    /// <param name="fixture">The fixture directory.</param>
    /// <param name="file">The file in it.</param>
    /// <returns>The absolute path.</returns>
    private static string Fixture(string fixture, string file) =>
        Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", "authoring", "import", fixture, file);

    /// <summary>The issues of a refused parse, as a failure message that echoes no cell text (S3).</summary>
    /// <param name="parse">The parse.</param>
    /// <returns>The message.</returns>
    private static string Describe(ImportParse parse) =>
        "expected an accepted file, got: " + string.Join(
            "; ",
            parse.Issues.Select(issue => $"{issue.Index?.ToString(CultureInfo.InvariantCulture) ?? "-"}{issue.Path}:{issue.Reason}"));
}
