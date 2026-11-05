using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Evidgate's own CI reports (xUnit v3, Vitest, Playwright; committed under scripts/dogfood/testdata exactly as the
/// runners emitted them) go through the parser profile customers' XML takes, <see cref="IParsing.ParseXmlReport"/>,
/// and the streamed document must use only the elements of the committed JUnit sample corpus. The report mapper
/// (plan 04) is not written yet: until it lands this is the strongest check the bytes can pass, and the same files
/// are the mapper's regression input.
/// </summary>
public sealed class DogfoodReportTests
{
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>The elements the committed fixtures/reports/junit/sample document uses.</summary>
    private static readonly HashSet<string> SampleElements = ["testsuites", "testsuite", "testcase", "failure", "skipped"];

    private static readonly string TestData = Path.Combine(RlsExceptionFile.RepositoryRoot, "scripts", "dogfood", "testdata");

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

    private static async Task<(int Exit, string Stderr)> RunCollect(string source, string output)
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
            _ = await process.StandardOutput.ReadToEndAsync(timeout.Token);
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
