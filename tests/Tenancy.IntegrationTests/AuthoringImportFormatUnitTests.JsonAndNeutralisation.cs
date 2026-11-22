using System.Text.Json;
using System.Text.RegularExpressions;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringImportFormatUnitTests
{
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

    [Fact]
    public void KeyMembers_TrailingLineFeed_Refused()
    {
        // The same exact-end rule the key and code carry applies to the two members that name another row of the same
        // file: a parent_key or item_key with a trailing line feed would name no row the file declares, so it is refused
        // at its own column rather than as an unknown parent later on.
        JsonFormat
            .Read(Utf8(
                """{"format":"authoring-import","version":1,"rows":[{"kind":"node","key":"n2","parent_key":"n1\n","node_kind":"feature","title":"Card payment"}]}"""))
            .Issues.ShouldBe([new ImportIssue(1, "/parent_key", "value_invalid")]);

        JsonFormat
            .Read(Utf8(
                """{"format":"authoring-import","version":1,"rows":[{"kind":"case","key":"c1","item_key":"i1\n","title":"T","priority":"high","steps":{"steps":[{"no":1,"action":"Pay","expected":"Refused"}]}}]}"""))
            .Issues.ShouldBe([new ImportIssue(1, "/item_key", "value_invalid")]);

        // The CSV carrier builds the same document, so the same cells answer the same way.
        var csvRows = string.Join(
            ',',
            "node",
            "n2",
            "\"n1\n\"",
            string.Empty,
            "feature",
            "Card payment",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "0");

        CsvFormat.Read(Utf8($"{Header}\r\n{csvRows}\r\n")).Issues
            .ShouldBe([new ImportIssue(1, "/parent_key", "value_invalid")]);
    }

    [Fact]
    public void AnchoredPatterns_MatchTheContract()
    {
        // ImportSchema applies the contract's key and code patterns with an exact end (\z) because the registry's
        // engine lets `$` match before a final line feed. The C# copies are therefore the contract's own text with one
        // terminator changed, and this pins them: a future contract edit fails here instead of silently leaving the
        // exact-end rule behind.
        var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RlsExceptionFile.RepositoryRoot, "contracts", "schemas", "authoring-import.v1.json")));

        var keyPattern = document.RootElement.GetProperty("$defs").GetProperty("key").GetProperty("pattern").GetString();
        var codePattern = document.RootElement
            .GetProperty("$defs")
            .GetProperty("case")
            .GetProperty("properties")
            .GetProperty("code")
            .GetProperty("pattern")
            .GetString();

        keyPattern.ShouldNotBeNull();
        codePattern.ShouldNotBeNull();

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key"] = keyPattern,
            ["parent_key"] = keyPattern,
            ["item_key"] = keyPattern,
            ["code"] = codePattern,
        };

        ImportSchema.Anchored.Select(entry => entry.Member).ShouldBe(["key", "parent_key", "item_key", "code"]);

        foreach (var (member, pointer, pattern) in ImportSchema.Anchored)
        {
            pointer.ShouldBe("/" + member);
            pattern.ToString().ShouldBe(
                expected[member].TrimEnd('$') + @"\z",
                $"{member} is the contract's pattern with an exact end, so the two cannot drift apart");
        }

        // And the two rules agree on what they refuse: the contract's pattern accepts the line feed the exact end
        // rejects, which is the whole reason this copy exists.
        Regex.IsMatch("n1\n", keyPattern!).ShouldBeTrue();
        ImportSchema.Anchored[0].Pattern.IsMatch("n1\n").ShouldBeFalse();
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
}
