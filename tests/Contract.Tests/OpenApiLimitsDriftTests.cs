// 04.1.3-T1 (AC3): the caps are published numbers, so the spec and the code must agree. The test reads the block
// the spec declares on `createRun` and the constants behind it, and fails on any difference in either direction — a
// cap added to the code but not published, or a published value the code no longer uses.
using System.Globalization;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Contracts;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The drift test between <c>contracts/openapi.yaml</c>'s <c>x-evidgate-limits</c> and the published defaults of
/// <see cref="IngestionLimits"/>.
/// </summary>
public sealed class OpenApiLimitsDriftTests
{
    /// <summary>Every cap the run-create operation publishes, with the constant that must equal it.</summary>
    private static readonly (string Name, long Value)[] PublishedDefaults =
    [
        ("manifest_bytes", IngestionLimits.DefaultMaxManifestBytes),
        ("manifest_executions", IngestionLimits.DefaultMaxManifestExecutions),
        ("manifest_variants", IngestionLimits.DefaultMaxManifestVariants),
        ("report_cases", IngestionLimits.DefaultMaxReportCases),
    ];

    /// <summary>
    /// AC3: each published value is the code's default, and the block publishes nothing the code does not have. A
    /// number that moves in one place and not the other fails here rather than in a client's first refusal.
    /// </summary>
    [Fact]
    public void OpenApi_LimitsMatchConstants()
    {
        var published = PublishedLimits();

        foreach (var (name, value) in PublishedDefaults)
        {
            published.ShouldContainKey(name, $"the spec must publish {name}");
            published[name].ShouldBe(value, $"x-evidgate-limits.{name} disagrees with the code default");
        }

        published.Count.ShouldBe(PublishedDefaults.Length, "the spec publishes a cap the code does not define");
    }

    /// <summary>
    /// The caps are documented on the operation that enforces them, and the error components name the detail members a
    /// refusal carries, so a client can read the limit without the source.
    /// </summary>
    [Fact]
    public void OpenApi_RunCreateDocumentsTheCapRefusals()
    {
        var text = File.ReadAllText(Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml"));

        text.ShouldContain("x-evidgate-limits:");
        text.ShouldContain(ApiErrorCode.CapExceeded);
        text.ShouldContain("manifest_bytes");
        text.ShouldContain("manifest_executions");
        text.ShouldContain("manifest_variants");
        text.ShouldContain("report_cases");
    }

    /// <summary>
    /// The defensive 409 of the producer-slot registration is documented on the createRun operation itself, under the
    /// wire code the registry returns. The registry-level proof is ProducerSlotTests; the create path issues a fresh run
    /// id, so no route test can reach it today.
    /// </summary>
    [Fact]
    public void OpenApi_RunCreateDocumentsTheProducerSlotsConflict()
    {
        // The path block also holds listRuns (GET); createRun (POST) is the part from its operationId on.
        var path = OperationBlock("createRun");
        var block = path[path.IndexOf("operationId: createRun", StringComparison.Ordinal)..];

        block.ShouldContain($"`{ApiErrorCode.ProducerSlotsConflict}`", Case.Sensitive);
        block.ShouldContain("'409':", Case.Sensitive, "the operation must declare a 409 response");
    }

    /// <summary>
    /// The manifest upload documents the cap it enforces: the configurable default and the option that lowers it, never
    /// the retired fixed 256 KiB.
    /// </summary>
    [Fact]
    public void OpenApi_ManifestUploadDocumentsTheConfigurableCap()
    {
        var block = OperationBlock("createManifest");

        block.ShouldNotContain("256 KiB", Case.Sensitive, "the retired fixed manifest cap must not come back");
        block.ShouldContain(
            IngestionLimits.DefaultMaxManifestBytes.ToString(CultureInfo.InvariantCulture),
            Case.Sensitive,
            "the description must state the published default");
        block.ShouldContain(
            "EVIDGATE__INGESTION__MAXMANIFESTBYTES",
            Case.Sensitive,
            "the description must name the option that lowers the cap");
    }

    /// <summary>The spec lines of one operation: from its path line to the next path line.</summary>
    private static string OperationBlock(string operationId)
    {
        var lines = File.ReadAllLines(Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml"));
        var operation = Array.FindIndex(lines, line => line.Trim() == $"operationId: {operationId}");
        operation.ShouldBeGreaterThanOrEqualTo(0, $"the spec must document {operationId}");

        var start = operation;
        while (start > 0 && !Regex.IsMatch(lines[start], @"^  /\S"))
        {
            start--;
        }

        var end = lines.Length;
        for (var index = operation + 1; index < lines.Length; index++)
        {
            if (Regex.IsMatch(lines[index], @"^  /\S"))
            {
                end = index;
                break;
            }
        }

        return string.Join('\n', lines[start..end]);
    }

    /// <summary>Reads the <c>x-evidgate-limits</c> mapping as the numbers a client is told.</summary>
    private static Dictionary<string, long> PublishedLimits()
    {
        var path = Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml");
        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, line => line.Trim() == "x-evidgate-limits:");

        start.ShouldBeGreaterThanOrEqualTo(0, "the spec must declare x-evidgate-limits");

        var indent = lines[start].Length - lines[start].TrimStart().Length;
        var limits = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (line.Length - line.TrimStart().Length <= indent)
            {
                break;
            }

            var parts = line.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
            parts.Length.ShouldBe(2, $"unexpected member {line}");
            limits[parts[0]] = long.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        return limits;
    }
}
