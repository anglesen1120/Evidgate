using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Evidgate's own CI reports (xUnit v3, Vitest, Playwright; scrubbed and serialised under
/// fixtures/reports/own-ci) go through the parser profile customers' XML takes, <see cref="IParsing.ParseXmlReport"/>,
/// and through the real JUnit mapper, which must report no discrepancy. The streamed document must also use only the
/// elements of the committed JUnit sample corpus and the system-out a failing Playwright case emits.
/// </summary>
public sealed partial class DogfoodReportTests
{
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>The elements the committed fixtures/reports/junit/sample document uses, plus the system-out a failing Playwright case emits for its attachments.</summary>
    private static readonly HashSet<string> SampleElements = ["testsuites", "testsuite", "testcase", "failure", "skipped", "system-out"];

    private static readonly string TestData = Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", "reports", "own-ci");

    /// <summary>One row per producer.</summary>
    public static TheoryData<string> Producers => [.. ProducerNames];

    private static readonly string[] ProducerNames = ["xunit", "vitest", "playwright"];

    /// <summary>The real report parses through the profile and has the sample corpus's shape and counts.</summary>
    [Theory]
    [MemberData(nameof(Producers))]
    public void Dogfood_Report_Parses(string producer)
    {
        using var content = File.OpenRead(Path.Combine(TestData, producer + ".junit.xml"));

        var result = new ParsingService().ParseXmlReport(content, MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        using var reader = result.Value!.OpenReader();
        var names = new List<string>();
        var declared = -1;
        var cases = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            names.Add(reader.LocalName);
            if (reader.LocalName == "testcase")
            {
                cases++;
                reader.GetAttribute("name").ShouldNotBeNullOrEmpty();
            }
            else if (reader.LocalName == "testsuites")
            {
                declared = int.Parse(reader.GetAttribute("tests")!, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        names[0].ShouldBe("testsuites");
        names.ShouldContain("testsuite");
        names.Distinct().ShouldBeSubsetOf(SampleElements);
        cases.ShouldBeGreaterThan(0);
        cases.ShouldBe(declared);
    }

    /// <summary>Evidgate's own xUnit v3 report maps through the real JUnit mapper with no discrepancy.</summary>
    [Fact]
    public void Dogfood_XunitReport_Parses() => AssertMapsCleanly("xunit");

    /// <summary>Evidgate's own Vitest report maps through the real JUnit mapper with no discrepancy.</summary>
    [Fact]
    public void Dogfood_VitestReport_Parses() => AssertMapsCleanly("vitest");

    /// <summary>Evidgate's own Playwright report maps through the real JUnit mapper with no discrepancy; a failing case would map as failed, not hidden.</summary>
    [Fact]
    public void Dogfood_PlaywrightReport_Parses() => AssertMapsCleanly("playwright");

    /// <summary>A truncated report ends as the profile's documented refusal, never an exception.</summary>
    [Fact]
    public void Dogfood_TruncatedReport_IsRefusedAsMalformed()
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestData, "xunit.junit.xml"));
        using var truncated = new MemoryStream(bytes, 0, bytes.Length / 2);

        var result = new ParsingService().ParseXmlReport(truncated, MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        result.Error.Details!.Single().Reason.ShouldBe(ParsingFailureReason.Malformed);
    }

    /// <summary>collect.sh assembles the upload set and its manifest lists all three producers.</summary>
    [Fact]
    public async Task Dogfood_Collect_ManifestListsAllProducers()
    {
        await WithTempDirectory(async output =>
        {
            var (exit, stderr) = await RunCollect(TestData, output);

            exit.ShouldBe(0, stderr);
            using var manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"), TestContext.Current.CancellationToken));
            manifest.RootElement.GetProperty("producers").EnumerateArray()
                .Select(p => p.GetProperty("producer").GetString())
                .ShouldBe(["xunit", "vitest", "playwright"]);
            Directory.GetFiles(output).Length.ShouldBe(4);
        });
    }

    /// <summary>The runner's machine name and checkout path never reach the upload set; mapped values are preserved.</summary>
    [Fact]
    public async Task Dogfood_Collect_ScrubsHostnameAndCheckoutPath()
    {
        var root = RlsExceptionFile.RepositoryRoot;
        await WithTempDirectory(async source =>
        {
            var expected = new Dictionary<string, string>();
            foreach (var name in ProducerNames)
            {
                var xml = await File.ReadAllTextAsync(Path.Combine(TestData, name + ".junit.xml"), TestContext.Current.CancellationToken);
                var hostile = System.Text.RegularExpressions.Regex.Replace(xml, "hostname=\"[^\"]*\"", "hostname=\"lan-runner-07\"");
                hostile = System.Text.RegularExpressions.Regex.Replace(
                    hostile,
                    "(<testsuite )([^>]*>)",
                    match => match.Groups[1].Value + "note=\"" + root + "/apps/web/e2e/a.spec.ts\" " + match.Groups[2].Value
                        + "<system-out>at " + root + "/apps/web/e2e/a.spec.ts:45:93</system-out>",
                    System.Text.RegularExpressions.RegexOptions.None);
                await File.WriteAllTextAsync(Path.Combine(source, name + ".junit.xml"), hostile, TestContext.Current.CancellationToken);
                expected[name] = System.Text.RegularExpressions.Regex.Replace(xml, "hostname=\"[^\"]*\"", "hostname=\"host\"");
            }

            await WithTempDirectory(async output =>
            {
                var (exit, stderr) = await RunCollect(source, output);

                exit.ShouldBe(0, stderr);
                foreach (var name in ProducerNames)
                {
                    var text = await File.ReadAllTextAsync(Path.Combine(output, name + ".junit.xml"), TestContext.Current.CancellationToken);
                    text.ShouldNotContain("lan-runner");
                    text.ShouldNotContain(root);
                    text.ShouldContain("note=\"/repo/apps/web/e2e/a.spec.ts\"");
                    text.ShouldContain("at /repo/apps/web/e2e/a.spec.ts:45:93");
                    var stripped = text.Replace("note=\"/repo/apps/web/e2e/a.spec.ts\" ", string.Empty, StringComparison.Ordinal)
                        .Replace("<system-out>at /repo/apps/web/e2e/a.spec.ts:45:93</system-out>", string.Empty, StringComparison.Ordinal);
                    XNode.DeepEquals(XDocument.Parse(stripped), XDocument.Parse(expected[name])).ShouldBeTrue();
                }

                using var manifest = JsonDocument.Parse(
                    await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"), TestContext.Current.CancellationToken));
                foreach (var entry in manifest.RootElement.GetProperty("producers").EnumerateArray())
                {
                    var written = await File.ReadAllBytesAsync(Path.Combine(output, entry.GetProperty("file").GetString()!), TestContext.Current.CancellationToken);
                    entry.GetProperty("sha256").GetString().ShouldBe(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(written)).ToLowerInvariant());
                    entry.GetProperty("bytes").GetInt32().ShouldBe(written.Length);
                }
            });
        });
    }

    /// <summary>A DOCTYPE in any encoding is refused: a UTF-16 document hides it from a byte search.</summary>
    [Fact]
    public async Task Dogfood_Collect_RefusesUtf16Doctype()
    {
        await WithTempDirectory(async source =>
        {
            foreach (var name in ProducerNames)
            {
                File.Copy(Path.Combine(TestData, name + ".junit.xml"), Path.Combine(source, name + ".junit.xml"));
            }

            const string Hostile = "<!DOCTYPE testsuites [<!ENTITY a \"a\">]><testsuites tests=\"1\"><testsuite><testcase name=\"&a;\"/></testsuite></testsuites>";
            await File.WriteAllBytesAsync(
                Path.Combine(source, "vitest.junit.xml"),
                [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(Hostile)],
                TestContext.Current.CancellationToken);

            await WithTempDirectory(async output =>
            {
                var (exit, stderr) = await RunCollect(source, output);

                exit.ShouldBe(1);
                stderr.ShouldContain("DOCTYPE");
            });
        });
    }

    [Fact]
    public async Task Dogfood_Collect_ScrubsOnlyTestsuiteHostnameAttributes()
    {
        const string Xml = """
            <testsuites><testsuite name="check hostname='x'" hostname = 'lan'>
            <testcase name="check hostname='x'" hostname="case"><system-out><![CDATA[<testsuite hostname="cdata">]]></system-out></testcase>
            <!-- <testsuite hostname="comment"> -->
            </testsuite></testsuites>
            """;
        await WithTempDirectory(async source =>
        {
            foreach (var name in ProducerNames)
            {
                await File.WriteAllTextAsync(Path.Combine(source, name + ".junit.xml"), Xml, TestContext.Current.CancellationToken);
            }

            await WithTempDirectory(async output =>
            {
                var (exit, stderr) = await RunCollect(source, output);

                exit.ShouldBe(0, stderr);
                var text = await File.ReadAllTextAsync(Path.Combine(output, "xunit.junit.xml"), TestContext.Current.CancellationToken);
                var doc = System.Xml.Linq.XDocument.Parse(text);
                doc.Descendants("testsuite").Single().Attribute("hostname")!.Value.ShouldBe("host");
                doc.Descendants("testcase").Single().Attribute("name")!.Value.ShouldBe("check hostname='x'");
                var expected = XDocument.Parse(Xml);
                expected.Descendants("testsuite").Single().SetAttributeValue("hostname", "host");
                expected.DescendantNodes().OfType<XComment>().Remove();
                expected.Descendants("system-out").Single().ReplaceNodes("<testsuite hostname=\"cdata\">");
                XNode.DeepEquals(doc, expected).ShouldBeTrue();
            });
        });
    }

    [Fact]
    public async Task Dogfood_Collect_RefusesUtf16Report()
    {
        const string Xml = "<testsuite hostname=\"lan\"><testcase name=\"case\"/></testsuite>";
        await WithTempDirectory(async source =>
        {
            foreach (var name in ProducerNames)
            {
                await File.WriteAllTextAsync(Path.Combine(source, name + ".junit.xml"), Xml, TestContext.Current.CancellationToken);
            }

            await WithTempDirectory(async output =>
            {
                var (exit, stderr) = await RunCollect(source, output);
                exit.ShouldBe(0, stderr);
            });
            await File.WriteAllBytesAsync(
                Path.Combine(source, "xunit.junit.xml"),
                [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(Xml)],
                TestContext.Current.CancellationToken);

            await WithTempDirectory(async output =>
            {
                var (exit, stderr) = await RunCollect(source, output);

                exit.ShouldBe(1);
                stderr.ShouldContain("xunit");
                stderr.ShouldContain("UTF-8");
                Directory.GetFiles(output).ShouldBeEmpty();
            });
        });
    }

    [Fact]
    public async Task Dogfood_Collect_RefusesInputOrSerialisedOutputOverCap()
    {
        const string Start = "<testsuite hostname=\"x\"><testcase name=\"case\"><system-out>";
        const string End = "</system-out></testcase></testsuite>";
        var padding = checked((int)MaxBytes) - Start.Length - End.Length;
        await WithReports(Start + new string('p', padding - 128) + End, async (source, output) =>
        {
            var path = Path.Combine(source, "xunit.junit.xml");
            var (exit, stderr) = await RunCollect(source, output);
            exit.ShouldBe(0, stderr);
            // Serialisation adds the XML declaration and the scrubbed hostname, so an input under
            // the cap can still overflow it.
            await File.WriteAllTextAsync(path, Start + new string('p', padding - 32) + End, TestContext.Current.CancellationToken);
            new FileInfo(path).Length.ShouldBe(MaxBytes - 32);
            await WithTempDirectory(async refused =>
            {
                var result = await RunCollect(source, refused);
                result.Exit.ShouldBe(1);
                result.Stderr.ShouldContain("xunit");
                result.Stderr.ShouldContain("exceeds");
                Directory.GetFiles(refused).ShouldBeEmpty();
            });
            await File.WriteAllTextAsync(path, Start + new string('p', padding + 1) + End, TestContext.Current.CancellationToken);
            new FileInfo(path).Length.ShouldBe(MaxBytes + 1);
            await WithTempDirectory(async refused =>
            {
                var result = await RunCollect(source, refused);
                result.Exit.ShouldBe(1);
                result.Stderr.ShouldContain("xunit");
                result.Stderr.ShouldContain("exceeds");
                Directory.GetFiles(refused).ShouldBeEmpty();
            });
        });
    }

    [Fact]
    public async Task Dogfood_Collect_RefusesForeignEncodingDeclaration()
    {
        const string Declaration = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>";
        const string Control = "<?xml version=\"1.0\" encoding=\"US-ASCII\"?>";
        const string Body = "<testsuite hostname=\"x\"><testcase name=\"case\"/></testsuite>";
        var bom = System.Text.Encoding.UTF8.GetPreamble();
        await WithTempDirectory(async source =>
        {
            foreach (var name in ProducerNames)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(source, name + ".junit.xml"),
                    [.. bom, .. System.Text.Encoding.UTF8.GetBytes(Control + Body)],
                    TestContext.Current.CancellationToken);
            }

            await WithTempDirectory(async output =>
            {
                var (exit, stderr) = await RunCollect(source, output);
                exit.ShouldBe(0, stderr);
            });
            await File.WriteAllBytesAsync(
                Path.Combine(source, "xunit.junit.xml"),
                [.. bom, .. System.Text.Encoding.UTF8.GetBytes(Declaration + Body)],
                TestContext.Current.CancellationToken);

            await WithTempDirectory(async output =>
            {
                var (exit, stderr) = await RunCollect(source, output);

                exit.ShouldBe(1);
                stderr.ShouldContain("xunit");
                stderr.ShouldContain("UTF-8");
                Directory.GetFiles(output).ShouldBeEmpty();
            });
        });
    }

    [Fact]
    public async Task Dogfood_Collect_MegabyteWhitespaceTag_FinishesWithinTwentySeconds()
    {
        var xml = "<testsuite" + new string(' ', 1024 * 1024) + "hostname='x'><testcase name='c'/></testsuite>";
        await WithReports(xml, async (source, output) =>
        {
            var elapsed = Stopwatch.StartNew();
            var (exit, stderr) = await RunCollect(source, output);
            elapsed.Stop();
            exit.ShouldBe(0, stderr);
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        });
    }

    [Fact]
    public async Task Dogfood_Collect_NonAsciiAttribute_PreservesParsedValue()
    {
        const string Xml = "<testsuite é=\"1 hostname='lan'\" hostname=\"x\"><testcase name=\"c\"/></testsuite>";
        await WithReports(Xml, async (source, output) =>
        {
            var (exit, stderr) = await RunCollect(source, output);
            exit.ShouldBe(0, stderr);
            var doc = XDocument.Load(Path.Combine(output, "xunit.junit.xml"));
            doc.Root!.Attribute("é")!.Value.ShouldBe("1 hostname='lan'");
            doc.Root.Attribute("hostname")!.Value.ShouldBe("host");
        });
    }

    [Fact]
    public async Task Dogfood_Collect_CharacterReferenceCheckoutPath_NeverPublishesPath()
    {
        var root = RlsExceptionFile.RepositoryRoot;
        var encoded = string.Concat(root.Select(c => "&#" + ((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture) + ";"));
        await WithReports("<testsuite><testcase name=\"" + encoded + "/case\"/></testsuite>", async (source, output) =>
        {
            var (exit, stderr) = await RunCollect(source, output, root);
            if (exit == 0)
            {
                XDocument.Load(Path.Combine(output, "xunit.junit.xml")).Root!.ToString().ShouldNotContain(root);
            }
            else
            {
                exit.ShouldBe(1, stderr);
                Directory.GetFiles(output).ShouldBeEmpty();
            }
        });
    }

    [Fact]
    public async Task Dogfood_Collect_MachineNameInTestcase_IsRefused()
    {
        var hostname = System.Net.Dns.GetHostName();
        if (hostname.Length == 0 || hostname.Equals("host", StringComparison.OrdinalIgnoreCase)) { return; }
        await AssertResidualRefused(hostname, inText: false);
    }

    [Fact]
    public async Task Dogfood_Collect_HomeDirectoryInText_IsRefused()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home) || home == "/") { return; }
        await AssertResidualRefused(home, inText: true);
    }

    private static async Task AssertResidualRefused(string value, bool inText)
    {
        // The name case keeps system-out out of the control so a host named "out" cannot refuse it.
        var Control = inText
            ? "<testsuite><testcase name=\"safe\"><system-out>safe</system-out></testcase></testsuite>"
            : "<testsuite><testcase name=\"safe\"/></testsuite>";
        await WithReports(Control, async (source, output) =>
        {
            var (exit, stderr) = await RunCollect(source, output);
            exit.ShouldBe(0, stderr);
            var doc = XDocument.Parse(Control);
            if (inText) { doc.Descendants("system-out").Single().Value = value; }
            else { doc.Descendants("testcase").Single().SetAttributeValue("name", value); }
            await File.WriteAllTextAsync(Path.Combine(source, "xunit.junit.xml"), doc.ToString(), TestContext.Current.CancellationToken);
            await WithTempDirectory(async refused =>
            {
                var result = await RunCollect(source, refused, value);
                result.Exit.ShouldBe(1);
                result.Stderr.ShouldContain("xunit");
                result.Stderr.ShouldContain("refusing");
                Directory.GetFiles(refused).ShouldBeEmpty();
            });
        });
    }

    private static Task WithReports(string xml, Func<string, string, Task> body) =>
        WithTempDirectory(async source =>
        {
            foreach (var name in ProducerNames)
            {
                await File.WriteAllBytesAsync(Path.Combine(source, name + ".junit.xml"),
                    System.Text.Encoding.UTF8.GetBytes(xml), TestContext.Current.CancellationToken);
            }
            await WithTempDirectory(output => body(source, output));
        });

    private static async Task WithTempDirectory(Func<string, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), "dogfood-collect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await body(path);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<(int Exit, string Stderr)> RunCollect(string source, string output, string? forbiddenValue = null)
    {
        var token = TestContext.Current.CancellationToken;
        using var process = Process.Start(new ProcessStartInfo("bash")
        {
            ArgumentList = { Path.Combine(RlsExceptionFile.RepositoryRoot, "scripts", "dogfood", "collect.sh"), "--src", source, "--out", output },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var error = await stderr;
            if (forbiddenValue is not null)
            {
                stdout.ShouldNotContain(forbiddenValue);
                error.ShouldNotContain(forbiddenValue);
            }
            if (process.ExitCode == 0)
            {
                foreach (var file in Directory.GetFiles(output, "*.xml")) { XDocument.Load(file); }
            }
            return (process.ExitCode, error);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
