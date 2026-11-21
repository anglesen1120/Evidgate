using System.Diagnostics;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class DogfoodReportTests
{
    private const string FakeHome = "/home/Fake-User-Zq";
    private const string FakeIdentifier = "Fake-Build-Host-Zq";
    private const string FakeHostname = "Injected-Runner-Host-Zq";

    /// <summary>The home directory is matched ignoring case: an upper-cased path still names the machine.</summary>
    [Fact]
    public async Task Dogfood_Collect_HomeDirectoryWithOtherCasing_IsRefused() =>
        await AssertRefusedAsync(Report(FakeHome.ToUpperInvariant() + "/work"), home: FakeHome);

    /// <summary>An extra identifier is matched ignoring case, from a fixed fake name that cannot fall below the length floor.</summary>
    [Fact]
    public async Task Dogfood_Collect_ExtraIdentifierWithOtherCasing_IsRefused() =>
        await AssertRefusedAsync(Report(FakeIdentifier.ToLowerInvariant()), extra: FakeIdentifier);

    /// <summary>Extra identifiers must not disable the independently supplied runner hostname.</summary>
    [Fact]
    public async Task Dogfood_Collect_ExtraIdentifier_DoesNotReplaceTheHostName()
    {
        await AssertRefusedAsync(Report(FakeHostname), extra: FakeIdentifier, hostname: FakeHostname);
        await AssertRefusedAsync(Report(FakeIdentifier), extra: FakeIdentifier, hostname: FakeHostname);
    }

    [Theory]
    [InlineData("element")]
    [InlineData("attribute")]
    [InlineData("element-namespace")]
    [InlineData("attribute-namespace")]
    public async Task Dogfood_Collect_IdentifierInXmlName_IsRefused(string surface)
    {
        await AssertAcceptedAsync(ReportWithNamedField(surface, "safe"), extra: FakeIdentifier);
        await AssertRefusedAsync(ReportWithNamedField(surface, FakeIdentifier), extra: FakeIdentifier);
    }

    [Theory]
    [InlineData("Fake%2DBuild%2DHost%2DZq")]
    [InlineData("%46ake-Build-Host-Zq")]
    [InlineData("Ｆａｋｅ－Ｂｕｉｌｄ－Ｈｏｓｔ－Ｚｑ")]
    [InlineData("％４６ake-Build-Host-Zq")]
    [InlineData("%EF%BC%A6ake-Build-Host-Zq")]
    public async Task Dogfood_Collect_EncodedIdentifier_IsRefused(string identifier)
    {
        await AssertAcceptedAsync(Report("safe"), extra: FakeIdentifier);
        await AssertRefusedAsync(Report(identifier), extra: FakeIdentifier);
    }

    [Fact]
    public async Task Dogfood_Collect_NormalizedSecret_IsRefused()
    {
        const string FullwidthSecret = "Ｆａｋｅ－Ｂｕｉｌｄ－Ｈｏｓｔ－Ｚｑ";
        await AssertAcceptedAsync(Report("safe"), extra: FullwidthSecret);
        await AssertRefusedAsync(Report(FakeIdentifier), extra: FullwidthSecret);
    }

    [Theory]
    [InlineData("\u00ad")]
    [InlineData("\u200b")]
    [InlineData("\u0301")]
    [InlineData("\u20dd")]
    public async Task Dogfood_Collect_IgnorableCharacterInIdentifier_IsRefused(string character)
    {
        await AssertAcceptedAsync(Report("safe"), extra: FakeIdentifier);
        var disguised = FakeIdentifier.Insert(5, character);
        await AssertRefusedAsync(Report(disguised), extra: FakeIdentifier);
        await AssertRefusedAsync(Report(FakeIdentifier), extra: disguised);
    }

    [Fact]
    public async Task Dogfood_Collect_IdentifierSplitAcrossMixedContent_IsRefused()
    {
        const string Control = "<testsuite><testcase><system-out>safe<detail/>text</system-out></testcase></testsuite>";
        const string Split = "<testsuite><testcase><system-out>Fake-<detail>Build</detail>-Host-Zq</system-out></testcase></testsuite>";
        await AssertAcceptedAsync(Control, extra: FakeIdentifier);
        await AssertRefusedAsync(Split, extra: FakeIdentifier);
    }

    [Theory]
    [InlineData("%zz")]
    [InlineData("trailing%")]
    [InlineData("%FF")]
    [InlineData("%C3%28")]
    public async Task Dogfood_Collect_MalformedPercentEncoding_IsRefused(string text)
    {
        await AssertAcceptedAsync(Report("safe"));
        await AssertRefusedAsync(Report(text));
    }

    [Fact]
    public async Task Dogfood_Collect_DoubleEncodedIdentifier_IsDecodedOnlyOnce() =>
        await AssertAcceptedAsync(Report("Fake%252DBuild%252DHost%252DZq"), extra: FakeIdentifier);

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

    [Fact]
    public async Task Dogfood_Collect_GreenRun_RemovesOnlyStaleUploadFiles()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            await WithTempDirectory(async outside =>
            {
                var target = Path.Combine(outside, "keep.txt");
                await File.WriteAllTextAsync(target, "outside", TestContext.Current.CancellationToken);
                string[] stale = ["old.junit.xml", "manifest.json", ".x.tmp-1", "notes.txt"];
                foreach (var name in stale)
                {
                    await File.WriteAllTextAsync(Path.Combine(output, name), "old", TestContext.Current.CancellationToken);
                }
                File.CreateSymbolicLink(Path.Combine(output, "linked.junit.xml"), target);
                File.CreateSymbolicLink(Path.Combine(output, ".broken.tmp-2"), Path.Combine(outside, "missing"));
                File.CreateSymbolicLink(Path.Combine(output, "notes-link"), target);
                var nested = Directory.CreateDirectory(Path.Combine(output, "nested.junit.xml")).FullName;
                await File.WriteAllTextAsync(Path.Combine(nested, "old.junit.xml"), "nested", TestContext.Current.CancellationToken);

                var result = await RunScriptAsync(source, output);
                result.Exit.ShouldBe(0, result.Stderr);
                Directory.GetFileSystemEntries(output).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
                    ["manifest.json", "nested.junit.xml", "notes-link", "notes.txt", "playwright.junit.xml", "vitest.junit.xml", "xunit.junit.xml"]);
                (await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken)).ShouldBe("outside");
                (await File.ReadAllTextAsync(Path.Combine(output, "notes.txt"), TestContext.Current.CancellationToken)).ShouldBe("old");
                (await File.ReadAllTextAsync(Path.Combine(nested, "old.junit.xml"), TestContext.Current.CancellationToken)).ShouldBe("nested");
                using var manifest = System.Text.Json.JsonDocument.Parse(
                    await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"), TestContext.Current.CancellationToken));
                manifest.RootElement.GetProperty("schema").GetString().ShouldBe("dogfood-collect/1");
            });
        });
    }

    [Fact]
    public async Task Dogfood_Collect_ReplacementFailure_LeavesOnlyCompleteReportsAndNoTemps()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            Directory.CreateDirectory(Path.Combine(output, "vitest.junit.xml"));
            var result = await RunScriptAsync(source, output);
            result.Exit.ShouldNotBe(0);
            Directory.GetFiles(output).Select(Path.GetFileName).ShouldBe(["xunit.junit.xml"]);
            XDocument.Load(Path.Combine(output, "xunit.junit.xml")).Root!
                .Element("testcase")!.Attribute("name")!.Value.ShouldBe("safe");
            Directory.Exists(Path.Combine(output, "vitest.junit.xml")).ShouldBeTrue();
            File.Exists(Path.Combine(output, "playwright.junit.xml")).ShouldBeFalse();
            File.Exists(Path.Combine(output, "manifest.json")).ShouldBeFalse();
            Directory.GetFileSystemEntries(output).Where(path => path.Contains(".tmp-", StringComparison.Ordinal)).ShouldBeEmpty();
        });
    }

    /// <summary>A failed second run must not leave the first run's manifest describing a mixed set.</summary>
    [Fact]
    public async Task Dogfood_Collect_ReplacementFailure_RemovesPreviousManifest()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            var first = await RunScriptAsync(source, output);
            first.Exit.ShouldBe(0, first.Stderr);
            File.Exists(Path.Combine(output, "manifest.json")).ShouldBeTrue();

            await File.WriteAllTextAsync(Path.Combine(source, "xunit.junit.xml"), Report("changed"), TestContext.Current.CancellationToken);
            File.Delete(Path.Combine(output, "vitest.junit.xml"));
            Directory.CreateDirectory(Path.Combine(output, "vitest.junit.xml"));

            var result = await RunScriptAsync(source, output);

            result.Exit.ShouldNotBe(0);
            File.Exists(Path.Combine(output, "manifest.json")).ShouldBeFalse();
            Directory.GetFileSystemEntries(output).Where(path => path.Contains(".tmp-", StringComparison.Ordinal)).ShouldBeEmpty();
        });
    }

    /// <summary>An --out that resolves to the --src directory is refused before anything is written.</summary>
    [Fact]
    public async Task Dogfood_Collect_OutputSymlinkToSourceDirectory_IsRefused()
    {
        var expected = Report("safe");
        await WithReports(expected, async (source, _) =>
        {
            await WithTempDirectory(async parent =>
            {
                var link = Path.Combine(parent, "out-link");
                Directory.CreateSymbolicLink(link, source);
                var before = Directory.GetFiles(source).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();

                var result = await RunScriptAsync(source, link);

                result.Exit.ShouldBe(2, result.Stderr);
                result.Stderr.ShouldContain("must differ");
                Directory.GetFiles(source).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ShouldBe(before);
                (await File.ReadAllTextAsync(Path.Combine(source, "xunit.junit.xml"), TestContext.Current.CancellationToken)).ShouldBe(expected);
                File.Exists(Path.Combine(source, "manifest.json")).ShouldBeFalse();
            });
        });
    }

    [Fact]
    public async Task Dogfood_Collect_AtomicReplacement_DoesNotWriteThroughExistingSymlink()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            await WithTempDirectory(async outside =>
            {
                var target = Path.Combine(outside, "keep.txt");
                await File.WriteAllTextAsync(target, "outside", TestContext.Current.CancellationToken);
                var report = Path.Combine(output, "xunit.junit.xml");
                File.CreateSymbolicLink(report, target);
                var result = await RunScriptAsync(source, output);
                result.Exit.ShouldBe(0, result.Stderr);
                new FileInfo(report).LinkTarget.ShouldBeNull();
                XDocument.Load(report).Root!.Element("testcase")!.Attribute("name")!.Value.ShouldBe("safe");
                (await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken)).ShouldBe("outside");
            });
        });
    }

    [Fact]
    public async Task Dogfood_Collect_PreExistingTempSymlink_IsNeverWrittenThrough()
    {
        await WithReports(Report("safe"), async (source, output) =>
        {
            await WithTempDirectory(async outside =>
            {
                var target = Path.Combine(outside, "keep.txt");
                await File.WriteAllTextAsync(target, "outside", TestContext.Current.CancellationToken);
                var planted = Path.Combine(output, ".xunit.junit.xml.tmp-0");
                File.CreateSymbolicLink(planted, target);

                var result = await RunScriptAsync(source, output);

                result.Exit.ShouldBe(0, result.Stderr);
                (await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken)).ShouldBe("outside");
                new FileInfo(Path.Combine(output, "xunit.junit.xml")).LinkTarget.ShouldBeNull();
                XDocument.Load(Path.Combine(output, "xunit.junit.xml")).Root!.Element("testcase")!
                    .Attribute("name")!.Value.ShouldBe("safe");
                File.Exists(planted).ShouldBeFalse("the planted link is a stale temp and is removed as a link");
            });
        });
    }

    [Fact]
    public async Task Dogfood_Collect_Refusal_LeavesExistingOutputUntouched()
    {
        await WithReports(Report(FakeIdentifier), async (source, output) =>
        {
            string[] names = ["old.junit.xml", "manifest.json", ".old.tmp-1", "notes.txt"];
            foreach (var name in names)
            {
                await File.WriteAllTextAsync(Path.Combine(output, name), name, TestContext.Current.CancellationToken);
            }
            var result = await RunScriptAsync(source, output, extra: FakeIdentifier);
            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("refusing");
            Directory.GetFiles(output).Select(Path.GetFileName).OrderBy(name => name).ShouldBe<string?>(names.OrderBy(name => name));
            foreach (var name in names)
            {
                (await File.ReadAllTextAsync(Path.Combine(output, name), TestContext.Current.CancellationToken)).ShouldBe(name);
            }
        });
    }

    private static string CollectScript => Path.Combine(RlsExceptionFile.RepositoryRoot, "scripts", "dogfood", "collect.sh");

    private static string Report(string caseName) =>
        new XDocument(new XElement("testsuite", new XElement("testcase", new XAttribute("name", caseName)))).ToString();

    private static string ReportWithNamedField(string surface, string identifier) => surface switch
    {
        "element" => $"<testsuite><testcase/><{identifier}/></testsuite>",
        "attribute" => $"<testsuite {identifier}=\"safe\"><testcase/></testsuite>",
        "element-namespace" => $"<testsuite xmlns:p=\"urn:{identifier}\"><testcase/><p:detail/></testsuite>",
        "attribute-namespace" => $"<testsuite xmlns:p=\"urn:{identifier}\" p:detail=\"safe\"><testcase/></testsuite>",
        _ => throw new ArgumentOutOfRangeException(nameof(surface)),
    };

    private static async Task AssertAcceptedAsync(string xml, string? extra = null)
    {
        await WithReports(xml, async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, extra: extra);
            result.Exit.ShouldBe(0, result.Stderr);
        });
    }

    private static async Task AssertRefusedAsync(
        string xml, string? home = null, string? extra = null, string? script = null, string? hostname = null)
    {
        await WithReports(xml, async (source, output) =>
        {
            var result = await RunScriptAsync(source, output, home, extra, script, hostname);
            result.Exit.ShouldBe(1, result.Stderr);
            result.Stderr.ShouldContain("refusing");
            Directory.GetFileSystemEntries(output).ShouldBeEmpty();
        });
    }

    private static async Task<(int Exit, string Stderr)> RunScriptAsync(
        string source, string output, string? home = null, string? extra = null, string? script = null, string? hostname = null)
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
        info.Environment["HOSTNAME"] = hostname ?? FakeHostname;
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
