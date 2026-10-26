using System.Text.Json;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T5.2 (AC16): the XLSX half of the hostile-input corpus — the archive caps, the DTD prohibition, the macro
/// refusal and the name normalisation, driven through <see cref="XlsxFormat.Read"/> over the committed fixtures in
/// <c>fixtures/hostile/authoring/</c> and their manifest.
/// </summary>
/// <remarks>
/// The corpus is flat like <c>fixtures/hostile/</c> itself: one committed file per guard plus <c>manifest.json</c> as
/// the index, so a fixture without a row and a row without a fixture both fail here. Every row asserts the reason
/// its guard produces and, on the refuse side, that the answer is a file-level issue with no index and the fixed
/// <c>/file</c> pointer — never an exception and never a cell (00 §5.7 S3).
/// </remarks>
public sealed partial class AuthoringImportHardeningTests
{
    /// <summary>The profile the corpus is read through — the real Evidence implementation.</summary>
    private static readonly IParsing XlsxParsing = new ParsingService();

    /// <summary>The corpus directory's absolute path.</summary>
    private static string XlsxFixtureDirectory =>
        Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", "hostile", "authoring");

    /// <summary>The manifest that indexes the corpus.</summary>
    private static string XlsxManifestPath => Path.Combine(XlsxFixtureDirectory, "manifest.json");

    /// <summary>The only members a corpus manifest row may carry (fixtures/hostile/README.md).</summary>
    private static readonly string[] ManifestMembers =
        ["file", "guard", "driven_through", "expected_reason", "expected_outcome"];

    [Fact]
    public void Xlsx_ZipBombEntryFloodDtdAndMacroRefused()
    {
        var rows = XlsxManifest();

        rows.ShouldNotBeEmpty("the manifest indexes the XLSX corpus");

        var reasons = new List<string>();

        foreach (var row in rows)
        {
            var bytes = File.ReadAllBytes(Path.Combine(XlsxFixtureDirectory, row.File));

            // The corpus exists to prove the reader refuses rather than throws: an escaping exception fails the case
            // by itself, so the read is made once, outside any assertion that could mask it.
            var parse = XlsxFormat.Read(bytes, XlsxParsing, TestContext.Current.CancellationToken);

            if (string.Equals(row.ExpectedOutcome, "accepted", StringComparison.Ordinal))
            {
                parse.IsSuccess.ShouldBeTrue($"{row.File} is the positive control and must be accepted");
                parse.Rows.ShouldNotBeEmpty($"{row.File} yields the rows it carries");

                continue;
            }

            parse.TooLarge.ShouldBeFalse($"{row.File} is refused as a file format, not as an over-long file");

            var issue = parse.Issues.ShouldHaveSingleItem($"{row.File} is refused exactly once");
            issue.Index.ShouldBeNull($"{row.File} is a file-level refusal, so it carries no row index");
            issue.Path.ShouldBe("/file", $"{row.File} carries the fixed file pointer, never a name or a value");
            issue.Reason.ShouldBe(row.ExpectedReason, $"{row.File} names the reason its guard produces");

            reasons.Add(issue.Reason);
        }

        // Each AC16 reason of the XLSX carrier is exercised by a fixture, so no guard can rot into a blanket refusal.
        foreach (var reason in new[]
        {
            "zip_ratio_exceeded",
            "zip_size_exceeded",
            "zip_entries_exceeded",
            "dtd_prohibited",
            "macro_prohibited",
            "unsupported_format",
        })
        {
            reasons.ShouldContain(reason, $"{reason} is proven by a corpus fixture");
        }
    }

    [Fact]
    public void XlsxFixtures_ManifestListsEveryFile()
    {
        var rows = XlsxManifest();
        var onDisk = Directory
            .GetFiles(XlsxFixtureDirectory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !string.Equals(name, "manifest.json", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        rows.Select(row => row.File).OrderBy(name => name, StringComparer.Ordinal).ToList().ShouldBe(
            onDisk,
            "the manifest is the index of the corpus: one row per committed file, in both directions");

        foreach (var row in rows)
        {
            row.Guard.StartsWith("xlsx-", StringComparison.Ordinal).ShouldBeTrue(
                $"{row.File} names the guard its row belongs to");
            row.DrivenThrough.ShouldBe("XlsxFormat.Read", $"{row.File} names the entry point it is driven through");
            File.Exists(Path.Combine(XlsxFixtureDirectory, row.File)).ShouldBeTrue($"{row.File} exists");
        }
    }

    /// <summary>Loads the corpus manifest: one row per fixture, only the documented members.</summary>
    /// <returns>The rows, in file order.</returns>
    private static List<XlsxFixtureRow> XlsxManifest()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(XlsxManifestPath));

        document.RootElement.ValueKind.ShouldBe(JsonValueKind.Array, "the manifest is a JSON array");

        var rows = new List<XlsxFixtureRow>();

        foreach (var row in document.RootElement.EnumerateArray())
        {
            foreach (var member in row.EnumerateObject())
            {
                ManifestMembers.ShouldContain(member.Name, $"the manifest has no unknown member ({member.Name})");
            }

            var reason = OptionalText(row, "expected_reason");
            var outcome = OptionalText(row, "expected_outcome");

            (reason is null ^ outcome is null).ShouldBeTrue(
                "a row carries exactly one of expected_reason / expected_outcome");

            rows.Add(new XlsxFixtureRow(
                Text(row, "file"),
                Text(row, "guard"),
                Text(row, "driven_through"),
                reason,
                outcome));
        }

        return rows;
    }

    /// <summary>Reads one required string member of a manifest row.</summary>
    /// <param name="row">The row.</param>
    /// <param name="name">The member name.</param>
    /// <returns>The member's text.</returns>
    private static string Text(JsonElement row, string name)
    {
        row.TryGetProperty(name, out var value).ShouldBeTrue($"a manifest row is missing {name}");
        value.ValueKind.ShouldBe(JsonValueKind.String, $"{name} is a JSON string");

        var text = value.GetString();

        text.ShouldNotBeNullOrWhiteSpace($"{name} is non-empty");

        return text!;
    }

    /// <summary>Reads one optional string member of a manifest row.</summary>
    /// <param name="row">The row.</param>
    /// <param name="name">The member name.</param>
    /// <returns>The member's text, or null when absent.</returns>
    private static string? OptionalText(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        value.ValueKind.ShouldBe(JsonValueKind.String, $"{name} is a JSON string when present");

        return value.GetString();
    }

    /// <summary>One manifest row of the XLSX corpus.</summary>
    /// <param name="File">The file name inside the corpus directory.</param>
    /// <param name="Guard">The guard id and its sentence.</param>
    /// <param name="DrivenThrough">The entry point the row is driven through.</param>
    /// <param name="ExpectedReason">The reason a refusal row must observe.</param>
    /// <param name="ExpectedOutcome">The outcome a positive-control row must observe.</param>
    private sealed record XlsxFixtureRow(
        string File,
        string Guard,
        string DrivenThrough,
        string? ExpectedReason,
        string? ExpectedOutcome);
}
