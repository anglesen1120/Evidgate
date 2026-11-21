using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class DogfoodReportTests
{
    /// <summary>A combining mark after a composable letter composes under NFKC, so it must be stripped before composition.</summary>
    [Theory]
    [InlineData("\u0301")]
    [InlineData("\u0308")]
    public async Task Dogfood_Collect_CombiningMarkAfterLetter_IsRefused(string mark)
    {
        await AssertAcceptedAsync(Report("safe"), extra: FakeIdentifier);
        var disguised = FakeIdentifier.Insert(2, mark);
        await AssertRefusedAsync(Report(disguised), extra: FakeIdentifier);
        await AssertRefusedAsync(Report(FakeIdentifier), extra: disguised);
    }

    /// <summary>Case folding can emit a mark (U+0130 folds to i plus U+0307); the strip runs after it.</summary>
    [Fact]
    public async Task Dogfood_Collect_DottedCapitalI_FoldsAndIsRefused()
    {
        const string Secret = "Fake-Bu\u0130ld-Host-Zq";
        await AssertRefusedAsync(Report("Fake-Build-Host-Zq"), extra: Secret);
        await AssertRefusedAsync(Report(Secret), extra: "Fake-Build-Host-Zq");
    }

    /// <summary>A trailing jamo outside the element must not recompose with the name's last syllable.</summary>
    [Fact]
    public async Task Dogfood_Collect_JamoAfterElementBoundary_DoesNotHideHostName()
    {
        const string Xml = "<testsuite><testcase><system-out>Runner<detail>\uac00</detail></system-out>\u11a8</testcase></testsuite>";
        await AssertRefusedAsync(Xml, hostname: "Runner\uac00");
    }

    /// <summary>The first DNS label of a dotted host name names the machine too; short and numeric labels are too common.</summary>
    [Theory]
    [InlineData("Injected-Runner-Zq.corp.local", "see Injected-Runner-Zq here", true)]
    [InlineData("Injected-Runner-Zq.corp.local", "see Injected-Runner-Zy here", false)]
    [InlineData("ab.corp.local", "ab", false)]
    [InlineData("192.168.1.5", "192", false)]
    [InlineData("abc.corp.local", "abc", true)]
    [InlineData("host.corp.local", "host", false)]
    [InlineData("localhost.localdomain", "http://localhost:4200", false)]
    public async Task Dogfood_Collect_HostLabelOfDottedName_IsRefusedFromThreeCharacters(string hostname, string text, bool refused)
    {
        if (refused)
        {
            await AssertRefusedAsync(Report(text), hostname: hostname);
        }
        else
        {
            await WithReports(Report(text), async (source, output) =>
            {
                var result = await RunScriptAsync(source, output, hostname: hostname);
                result.Exit.ShouldBe(0, result.Stderr);
            });
        }
    }

    /// <summary>All temp names of a later file taken: the run fails closed and removes the temps it already staged.</summary>
    [Fact]
    public async Task Dogfood_Collect_TempNamesExhausted_FailsClosedAndRemovesOwnTemps()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            var planted = Enumerable.Range(0, 100)
                .Select(attempt => Path.Combine(output, $".vitest.junit.xml.tmp-{attempt}")).ToArray();
            foreach (var path in planted)
            {
                await File.WriteAllTextAsync(path, "planted", TestContext.Current.CancellationToken);
            }

            var result = await RunScriptAsync(source, output);

            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("refusing to publish a partial set");
            Directory.GetFileSystemEntries(output).OrderBy(path => path, StringComparer.Ordinal)
                .ShouldBe(planted.OrderBy(path => path, StringComparer.Ordinal));
        });
    }

    /// <summary>Deep mixed content must be scanned once, not once per ancestor; the quadratic form takes minutes here.</summary>
    [Fact]
    public async Task Dogfood_Collect_DeepNonAsciiReport_FinishesWithinTenSeconds()
    {
        var xml = "<testsuite>" + string.Concat(Enumerable.Repeat("<a>", 500)) + "<testcase name='c'/>"
            + new string('é', 600_000) + string.Concat(Enumerable.Repeat("</a>", 500)) + "</testsuite>";
        await WithReports(xml, async (source, output) =>
        {
            var elapsed = Stopwatch.StartNew();
            var result = await RunScriptAsync(source, output);
            elapsed.Stop();
            result.Exit.ShouldBe(0, result.Stderr);
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        });
    }
}
