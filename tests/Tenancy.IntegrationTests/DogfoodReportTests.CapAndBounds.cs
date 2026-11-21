using System.Globalization;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class DogfoodReportTests
{
    /// <summary>Only 1 to 12 ASCII digits are a cap: non-ASCII digits (which str.isdigit accepts and int rejects), signs, spaces and an over-long number refuse with the message, never a traceback.</summary>
    [Theory]
    [InlineData("\u0663\u0660")]
    [InlineData("\u00b2")]
    [InlineData("-1")]
    [InlineData(" 5")]
    [InlineData("5\n")]
    [InlineData("1234567890123")]
    public async Task Dogfood_Collect_ScanCapOutsideAsciiDigitBound_RefusesWithMessage(string cap)
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, scanCapText: cap);
            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("EVIDGATE_COLLECT_SCAN_CAP must be");
            result.Stderr.ShouldNotContain("Traceback");
            Directory.GetFileSystemEntries(output).ShouldBeEmpty();
        });
    }

    /// <summary>The largest accepted cap is twelve nines; it changes nothing for a small report.</summary>
    [Fact]
    public async Task Dogfood_Collect_ScanCapAtTwelveDigits_Accepts()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, scanCapText: "999999999999");
            result.Exit.ShouldBe(0, result.Stderr);
        });
    }

    /// <summary>Normalisation is checked piece by piece: a 6 MB text of U+FDFA is refused after the first 64 Ki-character piece (18 characters each), so the longest string ever built is bounded by the piece and not by the report (the whole expansion would be 36 million characters).</summary>
    [Fact]
    public async Task Dogfood_Collect_OversizeExpansion_IsRefusedBeforeTheWholeTextIsNormalised()
    {
        const long PieceBound = 18L * 65_536;
        await WithReports(Report(new string('\ufdfa', 2_000_000)), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, scanStats: true);
            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("normalisation");
            var line = result.Stderr.Split('\n').Single(row => row.StartsWith("peak_normalised_chars=", StringComparison.Ordinal));
            var peak = long.Parse(line["peak_normalised_chars=".Length..], CultureInfo.InvariantCulture);
            peak.ShouldBeGreaterThan(0);
            peak.ShouldBeLessThanOrEqualTo(PieceBound);
            Directory.GetFileSystemEntries(output).ShouldBeEmpty();
        });
    }

    /// <summary>A name that straddles the 64 Ki-character normalisation piece edge is still matched, because matching runs on the joined result.</summary>
    [Theory]
    [InlineData(65_533)]
    [InlineData(65_536)]
    public async Task Dogfood_Collect_NameAcrossNormalisationPieceEdge_IsRefused(int padding)
    {
        var pad = new string('a', padding);
        await AssertAcceptedAsync(Report(pad + FakeIdentifier.Replace("Zq", "Zy", StringComparison.Ordinal)), extra: FakeIdentifier);
        await AssertRefusedAsync(Report(pad + FakeIdentifier), extra: FakeIdentifier);
    }

    /// <summary>Private-use code points (Co) render as nothing or as a font glyph and must not hide a name.</summary>
    [Theory]
    [InlineData("\ue000")]
    [InlineData("\uf8ff")]
    [InlineData("\U000f0000")]
    [InlineData("\U0010fffd")]
    public async Task Dogfood_Collect_PrivateUse_IsRefused(string privateUse)
    {
        await AssertAcceptedAsync(Report(FakeIdentifier.Insert(4, privateUse).Replace("Zq", "Zy", StringComparison.Ordinal)), extra: FakeIdentifier);
        var disguised = FakeIdentifier.Insert(4, privateUse);
        await AssertRefusedAsync(Report(disguised), extra: FakeIdentifier);
        await AssertRefusedAsync(Report(FakeIdentifier), extra: disguised);
    }

    /// <summary>The percent-decoded reading of a value is a second match_form call on report text and is counted: the same file without escapes counts less.</summary>
    [Fact]
    public async Task Dogfood_Collect_DecodedReading_IsCountedByTheScanCounter()
    {
        const int Units = 30_000;
        var plain = ScannedFor(string.Concat(Enumerable.Repeat("A", 3 * Units)));
        var escaped = ScannedFor(string.Concat(Enumerable.Repeat("%41", Units)));

        (await escaped).ShouldBeGreaterThanOrEqualTo(await plain + 2L * Units * 3);
    }

    private static async Task<long> ScannedFor(string text)
    {
        long scanned = 0;
        var xml = "<testsuite><testcase name='c'>" + text + "</testcase></testsuite>";
        await WithReports(xml, async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, scanStats: true);
            result.Exit.ShouldBe(0, result.Stderr);
            var line = result.Stderr.Split('\n').Single(row => row.StartsWith("scanned_chars=", StringComparison.Ordinal));
            scanned = long.Parse(line["scanned_chars=".Length..], CultureInfo.InvariantCulture);
        });
        return scanned;
    }
}
