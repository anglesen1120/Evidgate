using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The metering boundary of the accepted-batch emitter, exercised twice per rule: synthetic sources that must produce a
/// violation, and the real <c>src/</c> tree that must stay clean.
/// </summary>
public class UsageMeteringBoundaryTests
{
    /// <summary>The smallest tree that still proves the reader reached the real sources.</summary>
    private const int MinimumScannedFiles = 400;

    [Fact]
    public void Real_Solution_Names_The_Ledger_Only_Where_It_Is_Owned_Or_Called()
    {
        var files = SourceFiles();

        files.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumScannedFiles,
            $"only {files.Count} src/**/*.cs files were scanned; the generated-file filter or the repo root is hiding the tree");

        UsageMeteringRules.FindLedgerReferenceViolations(files)
            .ShouldBeEmpty(string.Join(Environment.NewLine, UsageMeteringRules.FindLedgerReferenceViolations(files)));

        // Non-vacuity: the rule sees both the owner's call and the ingest caller it was written for.
        files.ShouldContain(file => file.Path == UsageMeteringRules.MeteringHandlerPath && file.Text.Contains("IUsageLedger", StringComparison.Ordinal));
    }

    [Fact]
    public void Ledger_Reference_Outside_The_Allowed_Modules_Is_A_Violation()
    {
        var seeded = new List<(string Path, string Text)>
        {
            ("src/Modules/Evidgate.Modules.Policy/Application/Metering.cs", "internal sealed class X { IUsageLedger ledger; }"),
            ("src/Modules/Evidgate.Modules.Ingestion/Application/MeterEventBatchTaskHandler.cs", "IUsageLedger ledger;"),
            ("src/Evidgate.Api/ApiHost.cs", "// the IUsageLedger surface the metering callers resolve"),
        };

        var violations = UsageMeteringRules.FindLedgerReferenceViolations(seeded);

        var violation = violations.ShouldHaveSingleItem();
        violation.ShouldContain("Evidgate.Modules.Policy");
        violation.ShouldNotContain("Evidgate.Modules.Ingestion");
        // A comment naming the ledger on a composition root is not a call: the rule scans code, not prose.
        violation.ShouldNotContain("Evidgate.Api");
    }

    [Fact]
    public void Metering_Handler_Reaches_No_Http_Client()
    {
        var handler = SourceFiles().Where(file => file.Path == UsageMeteringRules.MeteringHandlerPath).ShouldHaveSingleItem();

        UsageMeteringRules.FindNetworkViolations(handler.Path, handler.Text).ShouldBeEmpty();

        // The negative control: the same rule fires on the reference it exists to ban.
        var violations = UsageMeteringRules.FindNetworkViolations(
            "src/Modules/Evidgate.Modules.Ingestion/Application/MeterEventBatchTaskHandler.cs",
            "internal sealed class X { private readonly HttpClient client; }");

        violations.ShouldHaveSingleItem().ShouldContain("HttpClient");
    }

    [Fact]
    public void Only_The_Ingest_Commit_And_Its_Handler_Produce_The_Usage_Task()
    {
        var files = SourceFiles();

        UsageMeteringRules.FindEmitterViolations(files).ShouldBeEmpty();

        // Non-vacuity: both names of the emitter set are in the scanned tree, so an empty violation list means clean.
        files.ShouldContain(file => file.Path.EndsWith("IngestEventBatch.cs", StringComparison.Ordinal)
            && file.Text.Contains("usage.batch_accepted", StringComparison.Ordinal));

        // A reprocess path that started metering would be a second producer, and this is what would catch it.
        var withReprocess = files
            .Append((
                "src/Modules/Evidgate.Modules.Ingestion/Application/ReprocessReportTask.cs",
                "await outbox.EnqueueAsync(\"usage.batch_accepted\", payload, agent: null);"))
            .ToList();

        var violation = UsageMeteringRules.FindEmitterViolations(withReprocess).ShouldHaveSingleItem();
        violation.ShouldContain("ReprocessReportTask.cs");
    }

    [Fact]
    public void No_Http_Surface_Calls_The_Ledger_Directly()
    {
        var files = SourceFiles();

        // Non-vacuity: the scan really sees the modules' endpoint files, so an empty result means "no route meters",
        // not "no endpoints were read".
        files.ShouldContain(file => UsageMeteringRules.IsEndpoint(file.Path, file.Text));

        UsageMeteringRules.FindEndpointMeteringViolations(files)
            .ShouldBeEmpty(string.Join(Environment.NewLine, UsageMeteringRules.FindEndpointMeteringViolations(files)));
    }

    [Theory]
    [InlineData("src/Modules/Evidgate.Modules.Ingestion/Endpoints/UsageEndpoints.cs", "IUsageLedger ledger;")]
    [InlineData("src/Modules/Evidgate.Modules.Usage/Endpoints/UsageRoutes.cs", "IUsageLedger ledger;")]
    [InlineData(
        "src/Modules/Evidgate.Modules.Usage/Application/RecordUsageBatch.cs",
        "routes.MapPost(\"/v1/usage\", async (IUsageLedger ledger) => await ledger.RecordAsync(null!, default));")]
    public void Endpoint_Metering_Is_A_Violation_Even_Inside_The_Allowed_Modules(string path, string text)
    {
        // Both modules may hold the ledger, but neither may expose it over HTTP: the rule fires on the endpoint shape,
        // not on the module.
        var violation = UsageMeteringRules.FindEndpointMeteringViolations([(path, text)]).ShouldHaveSingleItem();

        violation.ShouldContain(path);
        violation.ShouldContain("HTTP surface");
    }

    [Fact]
    public void Usage_Module_Route_File_That_Only_Enqueues_A_Command_Is_Clean()
    {
        // The shape the real usage route has: the endpoint builds the command and the command calls the ledger.
        (string Path, string Text)[] files =
        [
            ("src/Modules/Evidgate.Modules.Usage/Endpoints/UsageEndpoints.cs", "group.MapPost(\"/v1/usage\", RecordAsync);"),
            ("src/Modules/Evidgate.Modules.Usage/Application/Commands/RecordUsageBatch.cs", "IUsageLedger ledger;"),
        ];

        UsageMeteringRules.FindEndpointMeteringViolations(files).ShouldBeEmpty();
    }

    /// <summary>Every hand-written <c>src/**/*.cs</c> file, repo-relative with '/'.</summary>
    private static List<(string Path, string Text)> SourceFiles()
    {
        var root = Path.Combine(SolutionLayout.RepoRoot, "src");

        return
        [
            .. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsGenerated(root, path))
                .Select(path => (Relative(path), File.ReadAllText(path)))
        ];
    }

    private static bool IsGenerated(string srcRoot, string path)
    {
        var relative = Path.GetRelativePath(srcRoot, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments[..^1].Any(segment =>
            string.Equals(segment, "bin", StringComparison.Ordinal)
            || string.Equals(segment, "obj", StringComparison.Ordinal));
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(SolutionLayout.RepoRoot, path).Replace('\\', '/');
}
