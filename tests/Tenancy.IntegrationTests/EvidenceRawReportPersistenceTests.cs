using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>raw_report</c> half of the <c>raw_report_parse</c> handler, against the shared migrated database: what each
/// outcome stores about the report itself, and when it stores nothing. A parsed report's row commits with its events,
/// a refused report's row commits with the one reconcile trigger it leaves, and a skipped attempt leaves no row at all.
/// </summary>
/// <remarks>
/// Rows are read back over the harness's owner connection, the way the events are read: the row is what a reviewer sees,
/// so the assertion is on the committed state rather than on the handler's answer.
/// </remarks>
public sealed partial class EvidenceRawReportParseTests
{
    /// <summary>
    /// A report the mapper resolves completely: one testcase that its declared counters also carry, so nothing explains
    /// a difference between the document and the events.
    /// </summary>
    private static readonly byte[] MappedBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="1" failures="0" errors="0" skipped="0">
            <testcase name="probe" time="0.012"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>One case the run planned nothing for and a declared total the document does not carry.</summary>
    private static readonly byte[] PartialBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="3">
            <testcase name="probe" time="0.012"/>
            <testcase classname="tests.test_payments" name="ghost_case" time="0.003"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>The mapped report re-uploaded with one duration changed: same identity, different payload hash.</summary>
    private static readonly byte[] ChangedMappedBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="1" failures="0" errors="0" skipped="0">
            <testcase name="probe" time="0.020"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>A document whose every testcase is unresolvable, so it maps nothing while declaring one case.</summary>
    private static readonly byte[] UnmappedBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="1">
            <testcase classname="tests.test_elsewhere" name="not_planned" time="0.004"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    [Fact]
    public async Task Mapped_Report_Stores_One_Row_With_Its_Declared_And_Observed_Counters()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MappedBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);

        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("mapped");
        Text(row, "reason").ShouldBeNull();
        Text(row, "producer_kind").ShouldBe(ProducerKind);
        Text(row, "parser_id").ShouldBe("junit-pytest");
        Text(row, "parser_version").ShouldBe("1.0.0");

        NullableNumber(row, "declared_tests").ShouldBe(1);
        NullableNumber(row, "declared_failures").ShouldBe(0);
        NullableNumber(row, "declared_errors").ShouldBe(0);
        NullableNumber(row, "declared_skipped").ShouldBe(0);
        Number(row, "observed_tests").ShouldBe(1);
        Number(row, "observed_failures").ShouldBe(0);
        Number(row, "observed_errors").ShouldBe(0);
        Number(row, "observed_skipped").ShouldBe(0);
        Discrepancies(row).ShouldBeEmpty();

        // The row is the event set's own record: it was written in the same transaction, so the two counts agree.
        (await CountAsync(world, "event", artifactId)).ShouldBe(Number(row, "observed_tests"));
    }

    [Fact]
    public async Task Partial_Report_Stores_The_Unmapped_Case_And_Count_Mismatch()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, PartialBody);

        await ParseAsync(world, artifactId, ProducerKind);

        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);

        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("partial");
        Text(row, "reason").ShouldBeNull();
        NullableNumber(row, "declared_tests").ShouldBe(3);
        NullableNumber(row, "declared_failures").ShouldBeNull();
        Number(row, "observed_tests").ShouldBe(1);
        DiscrepancyCount(row, "unmapped_case").ShouldBe(1);
        DiscrepancyCount(row, "count_mismatch").ShouldBe(1);
        Discrepancies(row).Length.ShouldBe(2);

        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
    }

    [Fact]
    public async Task Report_Whose_Every_Case_Is_Unmapped_Stores_A_Row_And_No_Event()
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifactId = await SeedArtifactAsync(world, UnmappedBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(0);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);

        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("partial");
        NullableNumber(row, "declared_tests").ShouldBe(1);
        Number(row, "observed_tests").ShouldBe(0);
        DiscrepancyCount(row, "unmapped_case").ShouldBe(1);
        Discrepancies(row).Length.ShouldBe(1);

        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "producer_slot")).ShouldBe(0);
        // The report moved the run on, so the commit's only outbox row is its queued -> running edge: no trigger and
        // nothing metered.
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(1);
        (await OutboxRowsAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(0);
    }

    [Fact]
    public async Task Dtd_Document_Stores_A_Malformed_Row_With_The_Profile_Reason()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, System.Text.Encoding.UTF8.GetBytes(DtdBody));

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        await AssertRefusedRowAsync(world, artifactId, outcome, ParsingFailureReason.DtdProhibited);
    }

    [Fact]
    public async Task Truncated_Document_Stores_A_Malformed_Row_With_The_Profile_Reason()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        await AssertRefusedRowAsync(world, artifactId, outcome, ParsingFailureReason.Malformed);
    }

    [Theory]
    [InlineData(nameof(ParseRunFailureKind.ExitedAbnormally), 3, ParseRunRejection.Crashed)]
    [InlineData(nameof(ParseRunFailureKind.WallClockExceeded), 137, ParseRunRejection.Timeout)]
    [InlineData(nameof(ParseRunFailureKind.ExitedAbnormally), 4, ParseRunRejection.Oom)]
    public async Task A_Failed_Child_Stores_One_Malformed_Row_With_Its_Reason(
        string kind,
        int exitCode,
        string reason)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(Enum.Parse<ParseRunFailureKind>(kind), exitCode, 0, 0),
        };

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runner: runner);

        await AssertRefusedRowAsync(world, artifactId, outcome, reason);
    }

    [Fact]
    public async Task Unknown_Producer_Kind_Stores_An_Unsupported_Row_Without_A_Parser()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, UnknownKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);

        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("unsupported");
        Text(row, "reason").ShouldBe(outcome.Reason);
        Text(row, "reason").ShouldBe("unknown_producer_kind");
        Text(row, "producer_kind").ShouldBe(UnknownKind);
        Text(row, "parser_id").ShouldBeNull();
        Text(row, "parser_version").ShouldBeNull();
        NullableNumber(row, "declared_tests").ShouldBeNull();
        Number(row, "observed_tests").ShouldBe(0);
        Discrepancies(row).ShouldBeEmpty();

        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "producer_slot")).ShouldBe(0);
    }

    [Fact]
    public async Task Registered_Kind_With_No_Loaded_Mapper_Stores_Parser_Unavailable()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var unloaded = await SeedUnloadedParserAsync(world);

        var outcome = await ParseAsync(world, artifactId, unloaded.Kind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);

        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("unsupported");
        Text(row, "reason").ShouldBe("parser_unavailable");
        Text(row, "producer_kind").ShouldBe(unloaded.Kind);
        Text(row, "parser_id").ShouldBeNull();
        Text(row, "parser_version").ShouldBeNull();

        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task Replay_Of_A_Mapped_Report_Stores_No_Second_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MappedBody);

        var first = await ParseAsync(world, artifactId, ProducerKind);
        var second = await ParseAsync(world, artifactId, ProducerKind);

        first.Events.ShouldBe(1);
        second.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        second.Events.ShouldBe(0);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        Text(await RawReportAsync(world, artifactId), "outcome").ShouldBe("mapped");
    }

    [Fact]
    public async Task Replay_Of_A_Malformed_Report_Stores_No_Second_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);

        var first = await ParseAsync(world, artifactId, ProducerKind);
        var second = await ParseAsync(world, artifactId, ProducerKind);

        first.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        second.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        Text(await RawReportAsync(world, artifactId), "reason").ShouldBe(ParsingFailureReason.Malformed);
    }

    [Theory]
    [InlineData("update public.producer_slot")]
    [InlineData("insert into public.raw_report")]
    public async Task A_Write_That_Throws_Leaves_No_Row(string marker)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MappedBody);
        var throwing = new ThrowingTenantDb(world.Host.Services.GetRequiredService<ITenantDb>(), marker);

        await Should.ThrowAsync<InvalidOperationException>(
            () => ParseWithDbAsync(world, artifactId, throwing));

        // The row is the transaction's last insert, so a fault anywhere before it — or at it — commits nothing.
        (await RawReportCountAsync(world, artifactId)).ShouldBe(0);
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }

    [Fact]
    public async Task Replay_Whose_Bytes_Changed_Keeps_Only_The_First_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MappedBody);
        await ParseAsync(world, artifactId, ProducerKind);

        await StoreAsync(world, artifactId, ChangedMappedBody);
        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe("duplicate_different_payload");
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        Text(await RawReportAsync(world, artifactId), "outcome").ShouldBe("mapped");
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
    }

    [Fact]
    public async Task Foreign_Run_Report_Leaves_No_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var otherRun = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runId: otherRun);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe("foreign_run");
        (await RawReportCountAsync(world, artifactId)).ShouldBe(0);
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }

    [Fact]
    public async Task Closed_Run_Leaves_No_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MappedBody);
        await SetRunStateAsync(world, "update public.run set state = 'complete'");

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe(RunAdmissionPolicy.RunClosed);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(0);
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }

    [Fact]
    public async Task Two_Concurrent_Parses_Store_One_Row_And_One_Event_Set()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MappedBody);
        var payload = new ParseRawReportPayload(world.RunId, artifactId, ProducerKind);

        var outcomes = await Task.WhenAll(
            CreateHandler(world, runner: null).ExecuteAsync(payload, world.Org, Token),
            CreateHandler(world, runner: null).ExecuteAsync(payload, world.Org, Token));

        outcomes.ShouldAllBe(outcome => outcome.Status == ParseRawReportOutcome.Parsed);
        outcomes.Sum(outcome => outcome.Events).ShouldBe(1);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "case_attempt")).ShouldBe(1);
        (await CountAsync(world, "producer_slot")).ShouldBe(1);
        // The one committed set leaves its three rows: the run's queued -> running edge, the reconcile trigger and the
        // batch's usage row.
        (await CountAsync(world, "outbox")).ShouldBe(3);
    }

    /// <summary>One refusal's row: the outcome word, its reason, the parser release that judged it and nothing else.</summary>
    private async Task AssertRefusedRowAsync(
        World world,
        Guid artifactId,
        ParseRawReportOutcome outcome,
        string reason)
    {
        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe(reason);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);

        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("malformed");
        Text(row, "reason").ShouldBe(reason);
        Text(row, "parser_id").ShouldBe("junit-pytest");
        Text(row, "parser_version").ShouldBe("1.0.0");
        NullableNumber(row, "declared_tests").ShouldBeNull();
        NullableNumber(row, "declared_failures").ShouldBeNull();
        NullableNumber(row, "declared_errors").ShouldBeNull();
        NullableNumber(row, "declared_skipped").ShouldBeNull();
        Number(row, "observed_tests").ShouldBe(0);
        Number(row, "observed_failures").ShouldBe(0);
        Number(row, "observed_errors").ShouldBe(0);
        Number(row, "observed_skipped").ShouldBe(0);
        Discrepancies(row).ShouldBeEmpty();

        await AssertNothingWrittenAsync(world, artifactId, 1);
    }

    /// <summary>A registry row whose parser release has no loaded mapper, under a kind of its own.</summary>
    private async Task<UnloadedParser> SeedUnloadedParserAsync(World world)
    {
        var suffix = Convert.ToHexStringLower(Guid.CreateVersion7().ToByteArray())[..8];
        var parser = new UnloadedParser($"unloaded-junitxml-{suffix}", $"junit-unloaded-{suffix}", "9.9.9");

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.report_parser (id, org_id, parser_id, version, producer_kind, schema_version) "
            + $"values ('{Guid.CreateVersion7():D}', null, '{parser.ParserId}', '{parser.Version}', "
            + $"'{parser.Kind}', 1)");

        return parser;
    }

    /// <summary>The call the task consumer makes, with one write of it replaced by a throwing seam.</summary>
    private static async Task<ParseRawReportOutcome> ParseWithDbAsync(World world, Guid artifactId, ITenantDb db) =>
        await CreateHandler(world, runner: null, db: db).ExecuteAsync(
            new ParseRawReportPayload(world.RunId, artifactId, ProducerKind),
            world.Org,
            Token);

    private Task<long> RawReportCountAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.raw_report where artifact_id = '{artifactId:D}'");

    /// <summary>The report's one row, as the owner connection sees it after the attempt committed.</summary>
    private async Task<JsonElement> RawReportAsync(World world, Guid artifactId)
    {
        var json = await _fixture.ScalarAsync<string?>(
            world.Database,
            "select jsonb_build_object("
            + "'producer_kind', producer_kind, 'parser_id', parser_id, 'parser_version', parser_version, "
            + "'outcome', outcome, 'reason', reason, 'declared_tests', declared_tests, "
            + "'declared_failures', declared_failures, 'declared_errors', declared_errors, "
            + "'declared_skipped', declared_skipped, 'observed_tests', observed_tests, "
            + "'observed_failures', observed_failures, 'observed_errors', observed_errors, "
            + "'observed_skipped', observed_skipped, 'discrepancies', discrepancies)::text "
            + $"from public.raw_report where artifact_id = '{artifactId:D}'");

        if (json is null)
        {
            throw new ShouldAssertException($"artifact {artifactId:D} has no raw_report row");
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? Text(JsonElement row, string name) =>
        row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();

    private static int Number(JsonElement row, string name) => row.GetProperty(name).GetInt32();

    private static int? NullableNumber(JsonElement row, string name) =>
        row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetInt32();

    private static (string Code, int Count)[] Discrepancies(JsonElement row) =>
        row.GetProperty("discrepancies")
            .EnumerateArray()
            .Select(item => (
                Code: item.GetProperty("code").GetString() ?? string.Empty,
                Count: item.GetProperty("count").GetInt32()))
            .ToArray();

    private static int DiscrepancyCount(JsonElement row, string code) =>
        Discrepancies(row).Where(item => item.Code == code).Sum(item => item.Count);

    /// <summary>A registry release the module has no mapper for: a deployment mismatch, not an unknown kind.</summary>
    private sealed record UnloadedParser(string Kind, string ParserId, string Version);
}
