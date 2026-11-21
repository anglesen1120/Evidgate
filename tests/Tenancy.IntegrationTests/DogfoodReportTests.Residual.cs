using System.Diagnostics;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class DogfoodReportTests
{
    private const string FakeHome = "/home/Fake-User-Zq";
    private const string FakeIdentifier = "Fake-Build-Host-Zq";

    /// <summary>The home directory is matched ignoring case: an upper-cased path still names the machine.</summary>
    [Fact]
    public async Task Dogfood_Collect_HomeDirectoryWithOtherCasing_IsRefused() =>
        await AssertRefusedAsync(Report(FakeHome.ToUpperInvariant() + "/work"), home: FakeHome);

    /// <summary>An extra identifier is matched ignoring case, from a fixed fake name that cannot fall below the length floor.</summary>
    [Fact]
    public async Task Dogfood_Collect_ExtraIdentifierWithOtherCasing_IsRefused() =>
        await AssertRefusedAsync(Report(FakeIdentifier.ToLowerInvariant()), extra: FakeIdentifier);

    /// <summary>The extra identifiers only add: the real host name is still refused while the variable is set.</summary>
    [Fact]
    public async Task Dogfood_Collect_ExtraIdentifier_DoesNotReplaceTheRealHostName()
    {
        var hostname = System.Net.Dns.GetHostName();
        // Names under four characters are not an identifier the script guards, so there is nothing to keep.
        if (hostname.Length >= 4 && hostname != "host")
        {
            await AssertRefusedAsync(Report(hostname), extra: FakeIdentifier);
        }
        await AssertRefusedAsync(Report(FakeIdentifier), extra: FakeIdentifier);
    }

    /// <summary>The checkout path, as the script was invoked and as realpath resolves it, is refused where the scrub cannot replace it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dogfood_Collect_CheckoutPathResidual_IsRefused(bool realPath)
    {
        await WithTempDirectory(async temp =>
        {
            var real = Path.Combine(temp, "real-tree");
            Directory.CreateDirectory(Path.Combine(real, "scripts", "dogfood"));
            File.Copy(CollectScript, Path.Combine(real, "scripts", "dogfood", "collect.sh"));
            var link = Path.Combine(temp, "link-tree");
            Directory.CreateSymbolicLink(link, real);
            using var pwd = Process.Start(new ProcessStartInfo("bash") { ArgumentList = { "-c", "cd \"$0\" && pwd -P", link }, RedirectStandardOutput = true })!;
            var resolved = (await pwd.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Trim();
            resolved.ShouldNotBe(link, "the test needs a checkout path that differs from its realpath");

            // A trailing word character defeats the scrub's boundary, so only the residual check can catch it.
            var leaked = (realPath ? resolved : link) + "x";
            await AssertRefusedAsync(Report(leaked), script: Path.Combine(link, "scripts", "dogfood", "collect.sh"));
        });
    }

    /// <summary>A refusal on a later producer leaves nothing in --out: no earlier report and no manifest.</summary>
    [Fact]
    public async Task Dogfood_Collect_RefusalOnLaterProducer_LeavesOutputEmpty()
    {
        await WithTempDirectory(async source =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "xunit.junit.xml"), Report("safe"), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(source, "vitest.junit.xml"), Report("safe"), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(source, "playwright.junit.xml"), Report(FakeIdentifier), TestContext.Current.CancellationToken);
            await WithTempDirectory(async output =>
            {
                var result = await RunScriptAsync(source, output, extra: FakeIdentifier);
                result.Exit.ShouldBe(1, result.Stderr);
                result.Stderr.ShouldContain("playwright");
                Directory.GetFileSystemEntries(output).ShouldBeEmpty();
            });
        });
    }

    /// <summary>Control: with every producer clean the upload set and the manifest are written.</summary>
    [Fact]
    public async Task Dogfood_Collect_AllProducersClean_WritesReportsAndManifest()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, extra: FakeIdentifier);
            result.Exit.ShouldBe(0, result.Stderr);
            Directory.GetFiles(output).Select(Path.GetFileName).OrderBy(n => n).ShouldBe(
                ["manifest.json", "playwright.junit.xml", "vitest.junit.xml", "xunit.junit.xml"]);
        });
    }

    private static string CollectScript => Path.Combine(RlsExceptionFile.RepositoryRoot, "scripts", "dogfood", "collect.sh");

    private static string Report(string caseName) =>
        new XDocument(new XElement("testsuite", new XElement("testcase", new XAttribute("name", caseName)))).ToString();

    private static async Task AssertRefusedAsync(string xml, string? home = null, string? extra = null, string? script = null)
    {
        await WithReports(xml, async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, home, extra, script);
            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("refusing");
            Directory.GetFileSystemEntries(output).ShouldBeEmpty();
        });
    }

    private static async Task<(int Exit, string Stderr)> RunScriptAsync(
        string source, string output, string? home = null, string? extra = null, string? script = null)
    {
        var token = TestContext.Current.CancellationToken;
        var info = new ProcessStartInfo("bash")
        {
            ArgumentList = { script ?? CollectScript, "--src", source, "--out", output },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // A fixed HOME keeps the machine's real home out of these cases.
        info.Environment["HOME"] = home ?? "/nonexistent-home-zq";
        info.Environment["EVIDGATE_COLLECT_EXTRA_RESIDUAL"] = extra ?? "";
        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
