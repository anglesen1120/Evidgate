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

    /// <summary>The first DNS label of a dotted host name names the machine as a whole word when distinctive (5 characters, a digit or a hyphen); short, numeric and substring uses are too common.</summary>
    [Theory]
    [InlineData("Injected-Runner-Zq.corp.local", "see Injected-Runner-Zq here", true)]
    [InlineData("Injected-Runner-Zq.corp.local", "see Injected-Runner-Zy here", false)]
    [InlineData("ab.corp.local", "ab", false)]
    [InlineData("192.168.1.5", "192", false)]
    [InlineData("abc.corp.local", "abc", false)]
    [InlineData("app.corp.local", "an app and git", false)]
    [InlineData("git.corp.local", "git commit", false)]
    [InlineData("server.corp.local", "the server restarted", true)]
    [InlineData("server.corp.local", "Server.", true)]
    [InlineData("server.corp.local", "server-side", true)]
    [InlineData("server.corp.local", "serverless observer reserved", false)]
    [InlineData("server.corp.local", "se\u200brver", true)]
    [InlineData("node1.corp.local", "on node1", true)]
    [InlineData("node1.corp.local", "node12 mynode1", false)]
    [InlineData("ab-c.corp.local", "ab-c", true)]
    [InlineData("host.corp.local", "host", false)]
    [InlineData("localhost.localdomain", "http://localhost:4200", false)]
    [InlineData("app.corp.local", "x app.corp.local y", true)]
    [InlineData("buildbox-42.corp.local", "ci_buildbox-42_1", true)]
    [InlineData("server.corp.local", "ci_server_log", true)]
    [InlineData("server.corp.local", "ci_serverless", false)]
    public async Task Dogfood_Collect_HostLabelOfDottedName_IsRefusedAsDistinctiveWholeWord(string hostname, string text, bool refused)
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

    /// <summary>A full host name is refused whatever its length: one under 4 characters as a whole word only (not inside a longer word), and the literal placeholder "host" that the scrub writes never.</summary>
    [Theory]
    [InlineData("ci", "run on ci today", true)]
    [InlineData("ci", "CI", true)]
    [InlineData("ci", "civil special", false)]
    [InlineData("ab1", "x ab1", true)]
    [InlineData("ab1", "ab12 xab1", false)]
    [InlineData("srv", "srv-01", true)]
    [InlineData("srv", "ci_srv_log", true)]
    [InlineData("srv", "observer srvx", false)]
    [InlineData("host", "host", false)]
    [InlineData("HOST", "the host", false)]
    [InlineData("Runner-Zq-Long", "on runner-zq-long", true)]
    public async Task Dogfood_Collect_ShortFullHostName_IsRefusedAsWholeWord(string hostname, string text, bool refused)
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
