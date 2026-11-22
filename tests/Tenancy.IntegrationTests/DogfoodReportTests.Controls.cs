using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class DogfoodReportTests
{
    /// <summary>DEL, C1 controls (XML 1.0 accepts them) and the blank braille render as nothing and must not hide a name.</summary>
    [Theory]
    [InlineData("\u007f")]
    [InlineData("\u0080")]
    [InlineData("\u0085")]
    [InlineData("\u009f")]
    [InlineData("\u2800")]
    public async Task Dogfood_Collect_ControlAndBrailleBlank_IsRefused(string invisible)
    {
        await AssertAcceptedAsync(Report(FakeIdentifier.Insert(4, invisible).Replace("Zq", "Zy", StringComparison.Ordinal)), extra: FakeIdentifier);
        var disguised = FakeIdentifier.Insert(4, invisible);
        await AssertRefusedAsync(Report(disguised), extra: FakeIdentifier);
        await AssertRefusedAsync(Report(FakeIdentifier), extra: disguised);
    }

    /// <summary>A tab is whitespace that separates words, so it stays visible and a name split by it is a different name.</summary>
    [Fact]
    public async Task Dogfood_Collect_TabInsideName_IsAccepted()
    {
        await AssertAcceptedAsync(Report(FakeIdentifier.Insert(4, "\t")), extra: FakeIdentifier);
    }

    /// <summary>Unassigned code points outside the listed ranges are stripped by category, whatever the interpreter's Unicode version.</summary>
    [Theory]
    [InlineData("\u0378")]
    [InlineData("\u05ff")]
    public async Task Dogfood_Collect_UnassignedOutsideListedRanges_IsRefused(string unassigned)
    {
        await AssertAcceptedAsync(Report(FakeIdentifier.Insert(4, unassigned).Replace("Zq", "Zy", StringComparison.Ordinal)), extra: FakeIdentifier);
        var disguised = FakeIdentifier.Insert(4, unassigned);
        await AssertRefusedAsync(Report(disguised), extra: FakeIdentifier);
        await AssertRefusedAsync(Report(FakeIdentifier), extra: disguised);
    }

    /// <summary>NFKD of U+FDFA is 18 characters; output longer than four times the input plus 64 is refused after normalisation, before the strip passes.</summary>
    [Fact]
    public async Task Dogfood_Collect_ExpansionBeyondTheBound_IsRefused()
    {
        await WithReports(Report(new string('\ufdfa', 2_000)), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output);
            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("normalisation");
            result.Stderr.ShouldContain("refusing");
            Directory.GetFileSystemEntries(output).ShouldBeEmpty();
        });
    }

    /// <summary>Ligatures that expand twofold stay well inside the bound.</summary>
    [Fact]
    public async Task Dogfood_Collect_ModerateExpansion_IsAccepted()
    {
        await AssertAcceptedAsync(Report(new string('\ufb01', 2_000)));
    }

    /// <summary>The set is already published when the directory fsync fails: one warning line, still exit 0 and a complete manifest.</summary>
    [Fact]
    public async Task Dogfood_Collect_DirectoryFsyncFailure_WarnsAndStillPublishes()
    {
        const string Call = "os.fsync(directory)";
        var original = await File.ReadAllTextAsync(CollectScript, TestContext.Current.CancellationToken);
        original.ShouldContain(Call);
        await WithTempDirectory(async root =>
        {
            var script = Path.Combine(root, "scripts", "dogfood", "collect.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            await File.WriteAllTextAsync(
                script, original.Replace(Call, "raise OSError(5, 'EIO')", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            await WithReports(Report("safe"), async (source, output) =>
            {
                var result = await RunScriptAsync(source, output, script: script);
                result.Stderr.ShouldNotContain(root);
                result.Exit.ShouldBe(0, result.Stderr);
                result.Stderr.Split('\n').Count(row => row.Contains("directory fsync failed", StringComparison.Ordinal)).ShouldBe(1);
                File.Exists(Path.Combine(output, "manifest.json")).ShouldBeTrue();
            });
        });
    }

    /// <summary>A rescan regression stops at the scan cap by count, long before any timeout.</summary>
    [Fact]
    public async Task Dogfood_Collect_ScanCapExceeded_RefusesAtOnce()
    {
        var xml = "<testsuite>" + string.Concat(Enumerable.Repeat("<a>", 50)) + "<testcase name='c'/>"
            + new string('\u00e9', 20_000) + string.Concat(Enumerable.Repeat("</a>", 50)) + "</testsuite>";
        await WithReports(xml, async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, scanCap: 30_000);
            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("EVIDGATE_COLLECT_SCAN_CAP");
            Directory.GetFileSystemEntries(output).ShouldBeEmpty();
        });
    }

    /// <summary>A cap above the scanned count changes nothing, so the refusal above is the cap and not a side effect.</summary>
    [Fact]
    public async Task Dogfood_Collect_ScanCapAboveCount_Accepts()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, scanCap: 1_000_000);
            result.Exit.ShouldBe(0, result.Stderr);
        });
    }

    /// <summary>An unparsable cap refuses with a message instead of a traceback.</summary>
    [Fact]
    public async Task Dogfood_Collect_InvalidScanCap_RefusesWithMessage()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, scanCapText: "lots");
            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("EVIDGATE_COLLECT_SCAN_CAP must be");
            result.Stderr.ShouldNotContain("Traceback");
        });
    }

    /// <summary>An oversized host name (environment, not report) refuses with a message instead of a traceback.</summary>
    [Fact]
    public async Task Dogfood_Collect_ExpandingHostName_RefusesWithMessage()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, hostname: new string('\ufdfa', 5));
            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("host name expands");
            result.Stderr.ShouldNotContain("Traceback");
        });
    }
}
