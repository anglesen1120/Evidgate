// 04.4.1-T1.4 on the path the acceptance criteria name: the `raw_report_parse` task handler over the real Api
// composition, a real PostgreSQL and a real SeaweedFS container. The handler is resolved from the production service
// collection, so the tests also prove the registration (transactionless handler, resolver, mapper enumerable) rather
// than the handler's constructor. Expectations come from the plan (04.4.1 §2 AC3/AC4/AC7, §3-§5) and the coordinator
// decision that migration 0075 ships `report_parser` only (no `raw_report` row in this task).
using System.Globalization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>raw_report_parse</c> handler (04.4.1-T1) against the shared migrated database and one SeaweedFS container:
/// a resolvable report writes its mapped <c>test.finished</c> events and the freeze trigger's outbox row in one commit,
/// a replay writes nothing new, an unknown producer kind and a malformed document write nothing at all, and a report
/// whose artifact belongs to another run writes nothing either.
/// </summary>
/// <remarks>
/// The mapper and the parser registry are production: the built-in <c>junit-pytest/1.0.0</c> row comes from migration
/// 0075, and the run's one planned execution is seeded as the owner seam, the way the run's other parents are.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class EvidenceRawReportParseTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>The registry's producer kind for a pytest JUnit XML report (0075 seed).</summary>
    private const string ProducerKind = "pytest-junitxml";

    /// <summary>A producer kind no parser registers.</summary>
    private const string UnknownKind = "rube-goldberg-json";

    private const string XmlType = "application/xml";

    /// <summary>A well-formed execution key: sha256 hex (00 §7), the shape <c>planned_execution</c> requires.</summary>
    private static readonly string ExecutionKey = new('a', 64);

    /// <summary>
    /// One case that resolves (its <c>name</c> is the seeded case's name) and one that cannot (no planned execution
    /// carries that name), so one run proves both the insert and the <c>unmapped_case</c> path.
    /// </summary>
    private static readonly byte[] ReportBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="2">
            <testcase classname="tests.test_payments" name="probe" time="0.012"/>
            <testcase classname="tests.test_payments" name="ghost_case" time="0.003"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>A document that is not well formed: an unclosed element no parser can accept.</summary>
    private static readonly byte[] MalformedBody = "<testsuite><testcase name=\"probe\""u8.ToArray();

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record World(
        TenancyApiFactory Host,
        string Database,
        Guid Org,
        Guid Project,
        Guid RunId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }

    [Fact]
    public async Task Mapped_Report_Writes_One_Event_Per_Resolved_Case_And_One_Outbox_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "outbox")).ShouldBe(1);

        var parser = await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select payload #>> '{{ext,parser}}' from public.event where artifact_ids = array['{artifactId:D}'::uuid]");
        parser.ShouldBe("junit-pytest/1.0.0");

        var key = await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select execution_key from public.event where artifact_ids = array['{artifactId:D}'::uuid]");
        key.ShouldBe(ExecutionKey);
    }

    [Fact]
    public async Task Replay_Of_The_Same_Bytes_Writes_No_Second_Event_And_No_Second_Outbox_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var first = await ParseAsync(world, artifactId, ProducerKind);
        var second = await ParseAsync(world, artifactId, ProducerKind);

        first.Events.ShouldBe(1);
        second.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        second.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "outbox")).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_Producer_Kind_Is_Unsupported_And_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, UnknownKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        outcome.Reason.ShouldNotBeNullOrWhiteSpace();
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task Malformed_Report_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        // A document the parser refuses is a verdict; an infrastructure failure would throw instead, which the
        // handler's own contract keeps distinct so a broken store can never be reported as a malformed report.
        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task Artifact_Of_Another_Run_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var otherRun = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runId: otherRun);

        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task Report_Without_A_Planned_Execution_Writes_No_Event()
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "outbox")).ShouldBe(0);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database),
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-report-1"),
            _store.Configuration());

        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        return new World(host, database, org, project, runId);
    }

    /// <summary>
    /// One planned execution for the run, pointing at the case and revision <see cref="RunRowSeeder"/> seeds (name
    /// <c>probe</c>, path <c>a</c>): the report's <c>name="probe"</c> is the one hint that must resolve.
    /// </summary>
    private async Task SeedPlanAsync(World world)
    {
        var revision = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.test_revision where org_id = '{world.Org:D}' and file_path = 'a'");

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.planned_execution "
            + "(id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id) values "
            + $"('{Guid.CreateVersion7():D}', '{world.Org:D}', '{world.RunId:D}', '{ExecutionKey}', "
            + $"'{revision:D}', 'sha256:{new string('b', 64)}', 'staging', 'shard-1')");
    }

    /// <summary>One <c>ready</c> <c>raw_report</c> artifact whose final object the store really holds.</summary>
    private async Task<Guid> SeedArtifactAsync(World world, byte[] body)
    {
        var artifactId = Guid.CreateVersion7();
        var key = $"org/{world.Org:D}/project/{world.Project:D}/run/{world.RunId:D}/attempt/{ExecutionKey}/1/{artifactId:D}";
        var checksum = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(body));

        await _store.PutAsync(key, body);

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, "
            + "media_type, checksum, sensitivity, scan_state, state, bytes, failure_code) values "
            + $"('{artifactId:D}', '{world.Org:D}', '{world.Project:D}', '{world.RunId:D}', '{ExecutionKey}', 1, "
            + $"'{ArtifactKind.RawReport}', '{key}', '{XmlType}', '{checksum}', 'normal', 'pending', "
            + $"'{ArtifactState.Ready}', {body.LongLength.ToString(CultureInfo.InvariantCulture)}, null)");

        return artifactId;
    }

    /// <summary>
    /// The call the task consumer makes, minus the queue: the handler the module's own registration produced, so a
    /// missing registration fails here rather than in production.
    /// </summary>
    private static async Task<ParseRawReportOutcome> ParseAsync(
        World world,
        Guid artifactId,
        string producerKind,
        Guid? runId = null)
    {
        var handler = world.Host.Services.GetRequiredService<ParseRawReportHandler>();

        handler.ShouldBeAssignableTo<ITransactionlessTaskHandler<ParseRawReportPayload>>();

        return await handler.ExecuteAsync(
            new ParseRawReportPayload(runId ?? world.RunId, artifactId, producerKind),
            world.Org,
            Token);
    }

    private Task<long> CountAsync(World world, string table, Guid? artifactId = null) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            artifactId is null
                ? $"select count(*) from public.{table} where org_id = '{world.Org:D}'"
                : $"select count(*) from public.{table} where artifact_ids = array['{artifactId:D}'::uuid]");
}
