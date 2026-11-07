// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>GET /v1/runs/{r}/reports/{report_id}/comparison</c> through the real host and a real PostgreSQL: one stored
/// <c>raw_report</c> row (<c>0078</c>) beside the <c>test.finished</c> events its parse wrote, one sequence-ordered
/// case page, and the effective discrepancy codes of the report. A refused report serves no case, an unknown report,
/// another tenant's report, a report of another run and a project-scoped caller of another project are the one 404
/// <c>not_found</c> (00 §5.4: no existence oracle), and a <c>limit</c> outside 1–200 or a cursor this endpoint did not
/// issue is 422 <c>request_invalid</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class ReportComparisonTests
{
    private const string ProducerKind = "pytest-junitxml";
    private const string ParserId = "junit-pytest";
    private const string ParserVersion = "1.0.0";

    /// <summary>The release a reprocess pins: newer than the source, so it is a real second parse of the artifact.</summary>
    private const string ReprocessVersion = "1.1.0";
    private const string Checksum = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string PayloadHash = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private readonly PostgresTenancyFixture _fixture;

    public ReportComparisonTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One seeded report: the row the route names and the artifact its events are written under.</summary>
    private sealed record SeededReport(Guid Id, Guid ArtifactId);

    /// <summary>One event to seed: its sequence, the case label that mints its execution key, and its status word.</summary>
    private sealed record EventSeed(long Sequence, string CaseRef, string RawStatus);

    // ------------------------------------------------------------------ shape and counters

    /// <summary>
    /// AC: the body carries every contract member of the comparison and none of the artifact's content-bearing ones —
    /// no object key, no checksum, no byte count.
    /// </summary>
    [Fact]
    public async Task Comparison_Shape_HasEveryContractKeyAndNoContentBearingMember()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, runId, events: [new EventSeed(1, "probe", "passed")]);

        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Keys(answer.Body).ShouldBe(
        [
            "cases", "discrepancies", "next_cursor", "parser_id", "parser_version", "producer_kind",
            "raw_report_artifact_id", "reason", "report_id", "run_id", "summary",
        ]);
        Keys(Cases(answer)[0]).ShouldBe(["case_ref", "declared_status", "discrepancies", "observed_events"]);
        Keys(answer.Body.GetProperty("summary")).ShouldBe(
        [
            "declared_errors", "declared_failures", "declared_skipped", "declared_tests", "observed_errors",
            "observed_failures", "observed_skipped", "observed_tests", "status",
        ]);

        var text = answer.Body.GetRawText();
        text.ShouldNotContain("object_key");
        text.ShouldNotContain("checksum");
        text.ShouldNotContain("sha256:");
    }

    /// <summary>AC: the summary is the stored declared and observed counters beside the row's own outcome.</summary>
    [Fact]
    public async Task Comparison_DeclaredAndObserved_AreTheStoredCountersBesideTheMappedCases()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(
            world,
            runId,
            declaredTests: 3,
            declaredFailures: 1,
            observedTests: 2,
            observedFailures: 1,
            events: [new EventSeed(1, "probe", "passed"), new EventSeed(2, "probe-two", "failed")]);

        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var summary = answer.Body.GetProperty("summary");
        summary.GetProperty("declared_tests").GetInt32().ShouldBe(3);
        summary.GetProperty("declared_failures").GetInt32().ShouldBe(1);
        summary.GetProperty("declared_errors").ValueKind.ShouldBe(JsonValueKind.Null);
        summary.GetProperty("observed_tests").GetInt32().ShouldBe(2);
        summary.GetProperty("observed_failures").GetInt32().ShouldBe(1);
        summary.GetProperty("status").GetString().ShouldBe("mapped");

        answer.Body.GetProperty("report_id").GetGuid().ShouldBe(report.Id);
        answer.Body.GetProperty("run_id").GetGuid().ShouldBe(runId);
        answer.Body.GetProperty("raw_report_artifact_id").GetGuid().ShouldBe(report.ArtifactId);
        answer.Body.GetProperty("producer_kind").GetString().ShouldBe(ProducerKind);
        answer.Body.GetProperty("parser_id").GetString().ShouldBe(ParserId);
        answer.Body.GetProperty("parser_version").GetString().ShouldBe(ParserVersion);
        answer.Body.GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null);
        CaseRefs(answer).ShouldBe([ExecutionKeyFor("probe"), ExecutionKeyFor("probe-two")]);
        Cases(answer)[0].GetProperty("declared_status").GetString().ShouldBe("passed");
        Cases(answer)[0].GetProperty("observed_events").GetInt32().ShouldBe(1);
    }

    /// <summary>AC: one execution key is one case, and the row counts every event it has.</summary>
    [Fact]
    public async Task Comparison_RepeatedExecutionKey_IsOneCaseWithBothEventsCounted()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(
            world,
            runId,
            events: [new EventSeed(1, "probe", "passed"), new EventSeed(2, "probe", "failed")]);

        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Cases(answer).Length.ShouldBe(1);
        Cases(answer)[0].GetProperty("case_ref").GetString().ShouldBe(ExecutionKeyFor("probe"));
        Cases(answer)[0].GetProperty("observed_events").GetInt32().ShouldBe(2);
        Cases(answer)[0].GetProperty("declared_status").GetString().ShouldBe("passed");
    }

    // ------------------------------------------------------------------ discrepancy codes

    /// <summary>
    /// AC: a partial row's stored codes are served verbatim, distinct, in the canonical order the parse wrote — here
    /// the row stores <c>status_mismatch</c> first, and the answer names <c>count_mismatch</c> before it.
    /// </summary>
    [Fact]
    public async Task Comparison_PartialReport_ListsTheStoredCodesVerbatimInCanonicalOrder()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(
            world,
            runId,
            outcome: "partial",
            discrepancies: """[{"code":"status_mismatch","count":2},{"code":"count_mismatch","count":1}]""",
            events: [new EventSeed(1, "probe", "passed")]);

        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Codes(answer).ShouldBe(["count_mismatch", "status_mismatch"]);
    }

    /// <summary>AC: a malformed row serves the derived <c>malformed</c> code and no case at all.</summary>
    [Fact]
    public async Task Comparison_MalformedReport_DerivesTheMalformedCodeAndServesNoCase()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, runId, outcome: "malformed", reason: "malformed");

        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Codes(answer).ShouldBe(["malformed"]);
        answer.Body.GetProperty("reason").GetString().ShouldBe("malformed");
        Cases(answer).ShouldBeEmpty();
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>AC: an unsupported row keeps its reason word, and its empty stored list stays empty.</summary>
    [Fact]
    public async Task Comparison_UnsupportedReport_KeepsItsReasonAndServesNoCase()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, runId, outcome: "unsupported", reason: "unknown_producer_kind");

        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("reason").GetString().ShouldBe("unknown_producer_kind");
        answer.Body.GetProperty("parser_id").ValueKind.ShouldBe(JsonValueKind.Null);
        Codes(answer).ShouldBeEmpty();
        Cases(answer).ShouldBeEmpty();
    }

    /// <summary>
    /// AC: the stored codes carry no case attribution, so every case answers an empty list even when the report
    /// itself names <c>unmapped_case</c> and <c>status_mismatch</c>.
    /// </summary>
    [Fact]
    public async Task Comparison_Cases_CarryNoDiscrepancyEvenWhenTheReportStoresCodes()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(
            world,
            runId,
            outcome: "partial",
            discrepancies: """[{"code":"unmapped_case","count":1},{"code":"status_mismatch","count":1}]""",
            events: [new EventSeed(1, "probe", "passed"), new EventSeed(2, "probe-two", "failed")]);

        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Codes(answer).ShouldBe(["unmapped_case", "status_mismatch"]);
        foreach (var item in Cases(answer))
        {
            item.GetProperty("discrepancies").GetArrayLength().ShouldBe(0);
        }
    }
}
