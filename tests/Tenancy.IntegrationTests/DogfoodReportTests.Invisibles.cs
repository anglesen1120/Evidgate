using System.Globalization;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class DogfoodReportTests
{
    /// <summary>U+0345 is a mark that case folding turns into U+03B9; the strip must run again after the fold.</summary>
    [Fact]
    public async Task Dogfood_Collect_IotaSubscriptMark_IsRefused()
    {
        var disguised = FakeIdentifier.Insert(7, "\u0345");
        await AssertRefusedAsync(Report(disguised), extra: FakeIdentifier);
        await AssertRefusedAsync(Report(FakeIdentifier), extra: disguised);
    }

    /// <summary>Hangul fillers (Lo) and unassigned default-ignorable code points render as nothing and must not hide a name.</summary>
    [Theory]
    [InlineData("\u115f")]
    [InlineData("\u1160")]
    [InlineData("\u3164")]
    [InlineData("\uffa0")]
    [InlineData("\u2065")]
    [InlineData("\ufff0")]
    [InlineData("\ufff8")]
    [InlineData("\U000e0fff")]
    [InlineData("\U0001bca2")]
    public async Task Dogfood_Collect_FillerAndUnassignedIgnorable_IsRefused(string invisible)
    {
        var disguised = FakeIdentifier.Insert(4, invisible);
        await AssertRefusedAsync(Report(disguised), extra: FakeIdentifier);
        await AssertRefusedAsync(Report(FakeIdentifier), extra: disguised);
    }

    /// <summary>Visible text next to the stripped characters is still ordinary text: a different name stays accepted.</summary>
    [Fact]
    public async Task Dogfood_Collect_FillerInsideDifferentName_IsAccepted()
    {
        await AssertAcceptedAsync(Report(FakeIdentifier.Insert(4, "\u3164").Replace("Zq", "Zy", StringComparison.Ordinal)), extra: FakeIdentifier);
    }

    /// <summary>The host-name length test and the "host" skip use the match form, so an invisible cannot defeat either.</summary>
    [Theory]
    [InlineData("HO\u200bST", "a host b", false)]
    [InlineData("Local\u200bhost.corp.local", "http://localhost:4200", false)]
    [InlineData("ab\u3164c", "abc", false)]
    [InlineData("Run\u3164ner-Zq", "see Runner-Zq", true)]
    public async Task Dogfood_Collect_HostNameChecks_UseTheMatchForm(string hostname, string text, bool refused)
    {
        if (refused)
        {
            await AssertRefusedAsync(Report(text), hostname: hostname);
            return;
        }

        await WithReports(Report(text), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, hostname: hostname);
            result.Exit.ShouldBe(0, result.Stderr);
        });
    }

    /// <summary>Deep mixed content is scanned once: characters matched stay a small multiple of the payload, not depth times it.</summary>
    [Fact]
    public async Task Dogfood_Collect_DeepNonAsciiReport_ScansEachCharacterAtMostTwice()
    {
        const int Payload = 600_000;
        var xml = "<testsuite>" + string.Concat(Enumerable.Repeat("<a>", 500)) + "<testcase name='c'/>"
            + new string('é', Payload) + string.Concat(Enumerable.Repeat("</a>", 500)) + "</testsuite>";
        await WithReports(xml, async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, scanStats: true);
            result.Exit.ShouldBe(0, result.Stderr);
            var line = result.Stderr.Split('\n').Single(row => row.StartsWith("scanned_chars=", StringComparison.Ordinal));
            var scanned = long.Parse(line["scanned_chars=".Length..], CultureInfo.InvariantCulture);
            scanned.ShouldBeGreaterThanOrEqualTo(Payload);
            scanned.ShouldBeLessThan(2L * Payload + 10_000);
        });
    }
}
