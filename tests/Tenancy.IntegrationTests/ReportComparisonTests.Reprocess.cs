// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>GET /v1/runs/{r}/reports/{report_id}/comparison</c> of a report a reprocess wrote: the row answers the events of
/// the synthetic producer its own request owns, and a report no reprocess wrote — before or beside one — keeps
/// answering the events its parse wrote on the artifact.
/// </summary>
public sealed partial class ReportComparisonTests
{
    /// <summary>
    /// AC: a reprocessed report compares against its own producer. The cases are the reprocess's events, never the ones
    /// the original report's parse left on the same artifact.
    /// </summary>
    [Fact]
    public async Task Comparison_ReprocessedReport_ComparesAgainstTheReprocessProducer()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var original = await SeedReportAsync(world, runId, events: [new EventSeed(1, "original-probe", "passed")]);
        var reprocessed = await SeedReprocessAsync(
            world,
            runId,
            original,
            events: [new EventSeed(1, "reprocessed-probe", "failed"), new EventSeed(2, "reprocessed-two", "passed")]);

        var answer = await GetAsync(world, runId, reprocessed.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("report_id").GetGuid().ShouldBe(reprocessed.Id);
        answer.Body.GetProperty("raw_report_artifact_id").GetGuid().ShouldBe(original.ArtifactId);
        answer.Body.GetProperty("parser_id").GetString().ShouldBe(ReprocessParserId(world));
        answer.Body.GetProperty("parser_version").GetString().ShouldBe(ReprocessVersion);
        CaseRefs(answer).ShouldBe([ExecutionKeyFor("reprocessed-probe"), ExecutionKeyFor("reprocessed-two")]);
        Cases(answer).Length.ShouldBe(2);
        Cases(answer)[0].GetProperty("declared_status").GetString().ShouldBe("failed");
        Cases(answer)[1].GetProperty("declared_status").GetString().ShouldBe("passed");
        answer.Body.GetProperty("summary").GetProperty("observed_tests").GetInt32().ShouldBe(2);
    }

    /// <summary>
    /// AC: a normal report is unchanged. Its row answers the events its own parse wrote even while a reprocess of the
    /// same artifact answers beside it, so the newer release never displaces the original's comparison.
    /// </summary>
    [Fact]
    public async Task Comparison_OriginalReport_KeepsItsParseProducerBesideAReprocess()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var original = await SeedReportAsync(world, runId, events: [new EventSeed(1, "original-probe", "passed")]);
        await SeedReprocessAsync(
            world,
            runId,
            original,
            events: [new EventSeed(1, "reprocessed-probe", "failed")]);

        var answer = await GetAsync(world, runId, original.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("report_id").GetGuid().ShouldBe(original.Id);
        answer.Body.GetProperty("parser_version").GetString().ShouldBe(ParserVersion);
        CaseRefs(answer).ShouldBe([ExecutionKeyFor("original-probe")]);
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>AC: a report with no reprocess at all keeps the producer its parse wrote.</summary>
    [Fact]
    public async Task Comparison_ReportWithoutAReprocess_KeepsItsParseProducer()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(
            world,
            runId,
            events: [new EventSeed(1, "probe", "passed"), new EventSeed(2, "probe-two", "failed")]);

        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("parser_version").GetString().ShouldBe(ParserVersion);
        CaseRefs(answer).ShouldBe([ExecutionKeyFor("probe"), ExecutionKeyFor("probe-two")]);
        Cases(answer).Length.ShouldBe(2);
    }
}
