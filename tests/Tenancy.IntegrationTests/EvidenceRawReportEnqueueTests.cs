// The scheduling half of the raw-report mapping path: `POST /v1/artifacts/{a}/complete` writes the
// `raw_report_parse` row for a raw_report artifact when out-of-process parsing is composed, carrying the producer
// kind the run's manifest declares and an issue code when it declares none it can trust. Real PostgreSQL, real
// SeaweedFS and the real host; every assertion reads the outbox by SQL.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The enqueue half of the report mapping path: the completion of a <c>raw_report</c> artifact schedules
/// <c>raw_report_parse</c> under the same switch that schedules the artifact parse, and the payload carries the
/// dialect the run's manifest declares — or the closed issue code that stood in its place, so an unresolvable
/// report still leaves a report row instead of disappearing.
/// </summary>
/// <remarks>
/// The whole path is production: the mint, the presigned <c>PUT</c>, the server-side verification, the conditional
/// state change and the outbox writes in one transaction. The producer hint is seeded straight into the run's
/// manifest, which is where the completion reads it; the tests choose nothing else. The harness that seeds those
/// worlds is the type's other part, so neither file grows past the size bound.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class EvidenceRawReportEnqueueTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>A well-formed execution key: sha256 hex.</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string ProducerId = "shard-1";

    /// <summary>The dialect the seeded manifests declare; no parser row has to exist for the enqueue.</summary>
    private const string ProducerKind = "pytest-junitxml";

    /// <summary>The kind-policy cap the seeded policy carries; each report body is exactly this size.</summary>
    private const long Cap = 1024;

    private const string XmlType = "application/xml";

    private const string ZipType = "application/zip";

    private const string ReportParseTask = "raw_report_parse";

    private const string ProducerUnknown = "producer_unknown";

    private const string ProducerInvalid = "producer_invalid";

    private const string ProducerAmbiguous = "producer_ambiguous";

    /// <summary>The switch that composes out-of-process parsing; the completion reads it for both parse rows.</summary>
    private const string OutOfProcessKey = "EVIDGATE:PARSE:OUTOFPROCESS";

    /// <summary>A well-formed report body padded to exactly <see cref="Cap"/> bytes.</summary>
    private static readonly byte[] ReportBody =
        PadToCap("<?xml version=\"1.0\" encoding=\"utf-8\"?><report><testcase name=\"checkout\"/></report>");

    /// <summary>A zip-signature body for the control case: another kind never schedules a parse.</summary>
    private static readonly byte[] ZipBody =
        [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes(new string('A', (int)Cap - 4))];

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    /// <summary>
    /// One raw report of a run whose manifest declares a single hint: exactly one <c>raw_report_parse</c> row, with
    /// the run, the artifact and the declared kind, its own idempotency key, and no issue code.
    /// </summary>
    [Fact]
    public async Task Complete_Of_A_Declared_Kind_Schedules_One_Raw_Report_Parse()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);

        using var listener = CaptureIssues(out var issues);
        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, ReportParseTask, artifactId)).ShouldBe(1);
        (await PayloadFieldAsync(world, artifactId, "run_id")).ShouldBe(runId.ToString("D"));
        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBe(ProducerKind);
        (await PayloadFieldAsync(world, artifactId, "producer_issue")).ShouldBeNull();
        (await PayloadWithoutCarrierAsync(world, artifactId))
            .ShouldBe($"{{\"run_id\": \"{runId:D}\", \"artifact_id\": \"{artifactId:D}\", \"producer_kind\": \"{ProducerKind}\"}}");
        (await IdempotencyKeyAsync(world, artifactId))
            .ShouldBe($"evidence:artifact:{artifactId:D}:raw_report_parse");
        issues.ShouldContain("resolved", "the counter reports the lookup's outcome, never the hint itself");
    }

    /// <summary>
    /// A replay answers the stored outcome before it reaches the outbox, both with the same key and with a fresh one,
    /// so the artifact keeps exactly one mapping row.
    /// </summary>
    [Fact]
    public async Task Replayed_Completion_Schedules_No_Second_Raw_Report_Parse()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);
        var key = NewKey();

        await CompleteSucceedsAsync(world, artifactId, key);
        await CompleteSucceedsAsync(world, artifactId, key);
        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, ReportParseTask, artifactId))
            .ShouldBe(1, "the stored-outcome path returns before the outbox, and the key is the second guard");
    }

    /// <summary>
    /// With the flag off nothing is scheduled: the in-process composition is the rollback, so the completion writes
    /// only the rows it always wrote.
    /// </summary>
    [Fact]
    public async Task Complete_With_The_Flag_Off_Schedules_No_Raw_Report_Parse()
    {
        await using var world = await StartAsync(outOfProcess: false);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);

        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, ReportParseTask, artifactId)).ShouldBe(0);
        (await OutboxCountForRunAsync(world, runId)).ShouldBe(0);
    }

    /// <summary>
    /// Only <c>raw_report</c> has a mapping arm: the same completion for a trace schedules none, whatever its run's
    /// manifest declares.
    /// </summary>
    [Fact]
    public async Task Complete_Of_Another_Kind_Schedules_No_Raw_Report_Parse()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "trace", ZipType, ZipBody);

        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountForRunAsync(world, runId)).ShouldBe(0);
    }

    /// <summary>
    /// A manifest that declares no hint still schedules the mapping row: the payload carries the unknown code and no
    /// kind, so the handler can record why nothing was parsed.
    /// </summary>
    [Fact]
    public async Task Complete_Without_A_Declared_Hint_Schedules_The_Unknown_Issue()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);

        using var listener = CaptureIssues(out var issues);
        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, ReportParseTask, artifactId)).ShouldBe(1);
        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBeNull();
        (await PayloadFieldAsync(world, artifactId, "producer_issue")).ShouldBe(ProducerUnknown);
        issues.ShouldContain(ProducerUnknown);
    }

    /// <summary>
    /// Two shards that disagree on the hint declare no authoritative kind: the payload says ambiguous and carries no
    /// kind, because picking one would attribute a report to the wrong dialect.
    /// </summary>
    [Fact]
    public async Task Complete_With_Two_Distinct_Hints_Schedules_The_Ambiguous_Issue()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind, "vitest-junitxml");
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);

        using var listener = CaptureIssues(out var issues);
        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, ReportParseTask, artifactId)).ShouldBe(1);
        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBeNull();
        (await PayloadFieldAsync(world, artifactId, "producer_issue")).ShouldBe(ProducerAmbiguous);
        issues.ShouldContain(ProducerAmbiguous);
    }

    /// <summary>
    /// Two shards that declare the same hint agree on one kind: the read is over distinct values, not over shards.
    /// </summary>
    [Fact]
    public async Task Complete_With_Repeated_Hints_Schedules_The_One_Kind()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);

        await CompleteSucceedsAsync(world, artifactId);

        (await PayloadFieldAsync(world, artifactId, "producer_kind"))
            .ShouldBe(ProducerKind, "a repeated hint is one declared kind");
        (await PayloadFieldAsync(world, artifactId, "producer_issue")).ShouldBeNull();
    }

    /// <summary>
    /// A hint at the contract's 64-character ceiling, shaped like a statement fragment, never becomes a kind: the
    /// column's shape does not admit it, so the payload carries the invalid issue and no kind, and the report task has a
    /// refusal to write. The value stays untrusted data — it is a lookup key the lookup refused, never a metric tag.
    /// </summary>
    [Fact]
    public async Task Complete_With_A_Hostile_Hint_Schedules_The_Invalid_Issue()
    {
        var prefix = "'||(select pg_sleep(5))||'--";
        var hostile = prefix + new string('x', 64 - prefix.Length);
        hostile.Length.ShouldBe(64, "the contract's ceiling, the longest value the read accepts");

        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, hostile);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);

        using var listener = CaptureIssues(out var issues);
        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, ReportParseTask, artifactId)).ShouldBe(1);
        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBeNull();
        (await PayloadFieldAsync(world, artifactId, "producer_issue")).ShouldBe(ProducerInvalid);
        issues.ShouldContain(ProducerInvalid);
        issues.ShouldNotContain(hostile, "the hint is untrusted plan text and never becomes a metric tag");
    }

    /// <summary>
    /// A hint past the declared maximum never becomes a kind: a restored or hand-seeded manifest can hold one, the
    /// column's shape cannot store it, and dropping it would leave the report without its refusal.
    /// </summary>
    [Fact]
    public async Task Complete_With_An_Oversized_Hint_Schedules_The_Invalid_Issue()
    {
        var oversized = new string('k', 65);

        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, oversized);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);

        await CompleteSucceedsAsync(world, artifactId);

        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBeNull();
        (await PayloadFieldAsync(world, artifactId, "producer_issue")).ShouldBe(ProducerInvalid);
    }

    /// <summary>
    /// Another tenant's artifact is unreachable in this tenant's scope: the completion answers not found and nothing is
    /// scheduled, so no row crosses the org boundary either.
    /// </summary>
    [Fact]
    public async Task Complete_Of_Another_Tenants_Artifact_Schedules_Nothing()
    {
        await using var world = await StartAsync(outOfProcess: true);

        Guid foreignOrg;
        Guid foreignProject;
        using (var setup = world.Api.CreateClient())
        {
            var user = Guid.CreateVersion7();
            foreignOrg = await CreateOrgAsync(setup, user, Unique.Slug("beta"));
            foreignProject = await CreateProjectAsync(setup, user, foreignOrg, Unique.Slug("beta"));
        }

        // The foreign run's manifest does declare a kind, so a row could only be missing because the tenant scope
        // refused the read, never because the lookup had nothing to report.
        var foreignRun = await SeedRunForAsync(world, foreignOrg, foreignProject, ProducerKind);
        var artifactId = await InsertForeignArtifactAsync(world, foreignOrg, foreignProject, foreignRun);

        var answer = await CompleteAsync(world, artifactId);

        answer.Status.ShouldBe(HttpStatusCode.NotFound, "another tenant's row is invisible in this scope");
        answer.ErrorCode.ShouldBe("artifact_not_found");
        (await OutboxCountAnywhereAsync(world, artifactId))
            .ShouldBe(0, "a refused completion writes no row in either tenant");
        (await OutboxCountForRunAsync(world, foreignRun)).ShouldBe(0);
    }

    /// <summary>
    /// Every report of one run gets its own mapping row: the key is per artifact, so two uploads of the same run are
    /// two parses, one per report.
    /// </summary>
    [Fact]
    public async Task Two_Raw_Reports_Of_One_Run_Schedule_Two_Parses()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var first = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);
        var second = await MintAndUploadAsync(world, runId, "raw_report", XmlType, ReportBody);

        await CompleteSucceedsAsync(world, first);
        await CompleteSucceedsAsync(world, second);

        (await OutboxCountForRunAsync(world, runId)).ShouldBe(2);
        (await DistinctArtifactsScheduledAsync(world, runId)).ShouldBe(2);
    }
}
