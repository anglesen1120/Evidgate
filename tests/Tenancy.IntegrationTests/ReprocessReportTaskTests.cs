// The `ingest.reprocess_report` task handler over the real Worker composition, a real PostgreSQL and a real SeaweedFS
// container: the request row a command committed is re-read at the parser release it pinned, and the derived evidence
// lands under a producer identity of its own without moving the run or touching what the original report left behind.
// Expectations: a reprocess writes a closed synthetic slot of its own, binds no CI producer and leaves the run's
// completeness alone.
using System.Globalization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Reprocesses one stored report through the production handler: the pinned release maps the same bytes again, the events
/// carry <c>reprocess-&lt;parsing id&gt;</c> and their own 1..n sequences, one closed synthetic slot declares them, the
/// report's own row keeps the request's id, and the run — its state, its completeness, its original evidence — is left
/// exactly as it was.
/// </summary>
/// <remarks>
/// A reprocess pins a release the original report did not use, so the world registers a test-only <c>1.1.0</c> release
/// that delegates to the real <c>junit-pytest</c> mapper: the parse path stays production, and the pinned-version rule is
/// observable without shipping a second dialect.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class ReprocessReportTaskTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>The dialect the built-in release implements.</summary>
    private const string ParserId = "junit-pytest";

    /// <summary>The release the source report was parsed with.</summary>
    private const string SourceVersion = "1.0.0";

    /// <summary>The release a reprocess pins: newer than the source, same dialect.</summary>
    internal const string PinVersion = "1.1.0";

    /// <summary>The registry's producer kind for a pytest JUnit XML report.</summary>
    private const string ProducerKind = "pytest-junitxml";

    private const string XmlType = "application/xml";

    /// <summary>A well-formed execution key: sha256 hex, the shape <c>planned_execution</c> requires.</summary>
    private static readonly string ExecutionKey = new('a', 64);

    /// <summary>One case that resolves by its bare name (the seeded plan's case is named <c>probe</c>).</summary>
    private static readonly byte[] ReportBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="1">
            <testcase name="probe" time="0.012"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>A document whose DTD the parser profile refuses before it reads a single testcase.</summary>
    private static readonly byte[] DoctypeBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE testsuite [<!ENTITY boom "boom">]>
        <testsuite name="pytest" tests="1">
          <testcase name="probe" time="0.012">&boom;</testcase>
        </testsuite>
        """u8.ToArray();

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
    public async Task Reprocess_Writes_Its_Own_Events_Slot_And_Report_Row_Under_The_Pinned_Release()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        var outcome = await ReprocessAsync(world, parsingId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);

        var producerId = "reprocess-" + parsingId.ToString("D", CultureInfo.InvariantCulture);

        await AssertEventRowsAsync(world, producerId);
        await AssertReportRowAsync(world, parsingId);
        await AssertSyntheticSlotAsync(world, producerId);
        await AssertOneTriggerAsync(world, producerId);
        await AssertRunUntouchedAsync(world);
    }

    /// <summary>The events the reprocess wrote: its own producer, its own sequences and the pinned parser pair.</summary>
    private async Task AssertEventRowsAsync(World world, string producerId)
    {
        (await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select producer_id from public.event where org_id = '{world.Org:D}'")).ShouldBe(producerId);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select sequence from public.event where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select payload #>> '{{ext,parser}}' from public.event where org_id = '{world.Org:D}'"))
            .ShouldBe("junit-pytest/1.1.0");
    }

    /// <summary>The report's own row carries the request's id and the pinned pair, so the trigger and the row name one identity.</summary>
    private async Task AssertReportRowAsync(World world, Guid parsingId)
    {
        (await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select id::text from public.raw_report where org_id = '{world.Org:D}' and parser_version = '{PinVersion}'"))
            .ShouldBe(parsingId.ToString("D", CultureInfo.InvariantCulture));
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select outcome from public.raw_report where org_id = '{world.Org:D}' and id = '{parsingId:D}'"))
            .ShouldBe(RawReportOutcomes.Mapped);
    }

    /// <summary>The synthetic slot is closed, declared and unbound: nothing finalizes it, and no credential is behind it.</summary>
    private async Task AssertSyntheticSlotAsync(World world, string producerId)
    {
        (await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select producer_id from public.producer_slot where org_id = '{world.Org:D}'")).ShouldBe(producerId);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select closed_at is not null from public.producer_slot where org_id = '{world.Org:D}'")).ShouldBeTrue();
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select declared_last_sequence from public.producer_slot where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select declared_count from public.producer_slot where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select bound_token_jti from public.producer_slot where org_id = '{world.Org:D}'"))
            .ShouldBe("synthetic:junit-report");
    }

    /// <summary>One trigger names this producer and its own declaration, so the reconciler compares exactly these rows.</summary>
    private async Task AssertOneTriggerAsync(World world, string producerId) =>
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}' "
            + $"and (payload ->> 'producer_id') = '{producerId}'")).ShouldBe(1);

    /// <summary>The run never moved: no transition row and no batch receipt, because a reprocess is not an ingest route.</summary>
    private async Task AssertRunUntouchedAsync(World world)
    {
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunStateTransitionContract.OutboxTaskType}'")).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.run where id = '{world.RunId:D}'")).ShouldBe("collecting");
    }

    [Fact]
    public async Task Replay_Of_The_Same_Request_Writes_No_Second_Event_Or_Trigger()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        (await ReprocessAsync(world, parsingId)).Events.ShouldBe(1);
        var replay = await ReprocessAsync(world, parsingId);

        replay.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        replay.Events.ShouldBe(0);
        (await CountAsync(world, "event")).ShouldBe(1);
        (await CountAsync(world, "case_attempt")).ShouldBe(0, "a reprocess writes events and a revision, never an attempt");
        (await CountAsync(world, "producer_slot")).ShouldBe(1);
        (await CountAsync(world, "raw_report")).ShouldBe(2);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Reprocess_Leaves_The_Source_Report_And_The_Existing_Evidence_Untouched()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        var before = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select concat_ws('|', outcome, parser_version, observed_tests, discrepancies::text) "
            + $"from public.raw_report where id = '{sourceReportId:D}'");

        await ReprocessAsync(world, parsingId);

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select concat_ws('|', outcome, parser_version, observed_tests, discrepancies::text) "
            + $"from public.raw_report where id = '{sourceReportId:D}'")).ShouldBe(before);

        // The source report's own row and the report artifact are the only things the original parse left; both still
        // describe the same report, and the artifact is still the run's ready raw report.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.artifact where id = '{artifactId:D}'")).ShouldBe(ArtifactState.Ready);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.event where org_id = '{world.Org:D}' "
            + $"and producer_id not like 'reprocess-%'")).ShouldBe(0);
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

        // The pinned release's mapper: the real dialect's mapper under its own version, so the pinned-release rule is
        // observable without a second production dialect (the registry row naming that pair is written with the request).
        var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database),
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-reprocess-1"),
            _store.Configuration(),
            services: services => services.AddSingleton<IJUnitMapper>(
                factory => new PinnedReleaseMapper(
                    PytestJUnitMapper.Create(factory.GetRequiredService<IParsing>()))));

        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        // The run waits for collection: a reprocess must not move it, so the state a test starts from is the one it keeps.
        await _fixture.ExecuteAsync(
            database,
            $"update public.run set state = 'collecting' where id = '{runId:D}'");

        return new World(host, database, org, project, runId);
    }

    /// <summary>One planned execution for the run: the report's bare-name testcase is the one hint that must resolve.</summary>
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
    /// <param name="world">The world the report belongs to.</param>
    /// <param name="body">The report's bytes.</param>
    /// <param name="projectId">
    /// The project the row names, or null for the run's own. The column is not a key of the run's, so a row may name
    /// another project of the org and still be this run's raw report.
    /// </param>
    private async Task<Guid> SeedArtifactAsync(World world, byte[] body, Guid? projectId = null)
    {
        var artifactId = await SeedArtifactRowAsync(world, body, projectId);
        await _store.PutAsync(ObjectKeyFor(world, artifactId, projectId), body);

        return artifactId;
    }

    /// <summary>The artifact row alone, for the test that needs a row whose object the store never received.</summary>
    /// <param name="world">The world the report belongs to.</param>
    /// <param name="body">The report's bytes.</param>
    /// <param name="projectId">The project the row names, or null for the run's own.</param>
    private async Task<Guid> SeedArtifactRowAsync(World world, byte[] body, Guid? projectId = null)
    {
        var owner = projectId ?? world.Project;
        var artifactId = Guid.CreateVersion7();
        var key = ObjectKeyFor(world, artifactId, owner);
        var checksum = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(body));

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, "
            + "media_type, checksum, sensitivity, scan_state, state, bytes, failure_code) values "
            + $"('{artifactId:D}', '{world.Org:D}', '{owner:D}', '{world.RunId:D}', '{ExecutionKey}', 1, "
            + $"'{ArtifactKind.RawReport}', '{key}', '{XmlType}', '{checksum}', 'normal', 'pending', "
            + $"'{ArtifactState.Ready}', {body.LongLength.ToString(CultureInfo.InvariantCulture)}, null)");

        return artifactId;
    }

    /// <summary>The report row the original parse would have committed: the source a reprocess re-reads.</summary>
    private async Task<Guid> SeedSourceReportAsync(World world, Guid artifactId)
    {
        var id = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.raw_report (id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version, "
            + "outcome, reason, declared_tests, declared_failures, declared_errors, declared_skipped, "
            + "observed_tests, observed_failures, observed_errors, observed_skipped, discrepancies) values "
            + $"('{id:D}', '{world.Org:D}', '{world.RunId:D}', '{artifactId:D}', '{ProducerKind}', '{ParserId}', "
            + $"'{SourceVersion}', '{RawReportOutcomes.Mapped}', null, 1, 0, 0, 0, 1, 0, 0, 0, '[]'::jsonb)");

        return id;
    }

    /// <summary>
    /// The immutable request a command would have committed, with the id the task writes its report row under, and the
    /// release it pins registered as this org's own release under exactly that version: the catalogue's
    /// <c>(parser_id, version)</c> key is global, so the row is re-owned by the world that pins it and the global
    /// catalogue — and therefore every other suite's resolution of the built-in <c>1.0.0</c> — stays untouched. It is
    /// written (or re-dated) behind the built-in release on purpose: the registry resolves a producer kind to its
    /// <em>most recent</em> row, and the report a run already holds was parsed at the built-in <c>1.0.0</c>, so the newer
    /// release must never win that resolution — it is only named by the request, which pins its pair exactly.
    /// </summary>
    private async Task<Guid> SeedRequestAsync(World world, Guid artifactId, Guid sourceReportId, string pinVersion)
    {
        var id = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.report_parser "
            + "(id, org_id, parser_id, version, producer_kind, schema_version, notes, created_at) values "
            + $"('{Guid.CreateVersion7():D}', '{world.Org:D}', '{ParserId}', '{pinVersion}', '{ProducerKind}', 1, "
            + "'test release', '2000-01-01T00:00:00Z') "
            + "on conflict (parser_id, version) do update "
            + "set org_id = excluded.org_id, created_at = excluded.created_at");

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.raw_report_reprocess "
            + "(id, org_id, run_id, artifact_id, source_raw_report_id, parser_id, parser_version) values "
            + $"('{id:D}', '{world.Org:D}', '{world.RunId:D}', '{artifactId:D}', '{sourceReportId:D}', "
            + $"'{ParserId}', '{pinVersion}')");

        return id;
    }

    /// <summary>The call the task consumer makes, minus the queue.</summary>
    private static async Task<ParseRawReportOutcome> ReprocessAsync(World world, Guid parsingId) =>
        await CreateHandler(world).ExecuteAsync(new ReprocessReportPayload(parsingId), world.Org, Token);

    private static ReprocessReportHandler CreateHandler(World world)
    {
        var services = world.Host.Services;

        return new ReprocessReportHandler(
            services.GetRequiredService<ITenantScopeFactory>(),
            services.GetRequiredService<ITenantDbContextFactory<Evidgate.Modules.Evidence.Infrastructure.Persistence.EvidenceDbContext>>(),
            services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IObjectStorage>(),
            new EmulatedChildRunner(services.GetRequiredService<IParsing>()),
            services.GetRequiredService<IOptions<IngestionLimits>>(),
            services.GetServices<IJUnitMapper>(),
            services.GetRequiredService<IOutboxWriter>(),
            NullLogger<ReprocessReportHandler>.Instance);
    }

    private Task<long> CountAsync(World world, string table) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.{table} where org_id = '{world.Org:D}'");

    /// <summary>The final key the artifact row mints, so a test writes and the handler reads the same object.</summary>
    /// <param name="world">The world the report belongs to.</param>
    /// <param name="artifactId">The artifact whose key is minted.</param>
    /// <param name="projectId">The project the row names, or null for the run's own.</param>
    private static string ObjectKeyFor(World world, Guid artifactId, Guid? projectId = null) =>
        $"org/{world.Org:D}/project/{(projectId ?? world.Project):D}/run/{world.RunId:D}/attempt/{ExecutionKey}/1/{artifactId:D}";
}

/// <summary>
/// The test-only newer release: the production dialect's mapping under its own version, so a request that pins a newer
/// release resolves a real mapper and the pinned pair is what the events and the row are stamped with.
/// </summary>
internal sealed class PinnedReleaseMapper(IJUnitMapper inner) : IJUnitMapper
{
    /// <inheritdoc />
    public string ParserId => inner.ParserId;

    /// <inheritdoc />
    public string Version => ReprocessReportTaskTests.PinVersion;

    /// <inheritdoc />
    public JUnitMapResult Map(Stream xml, JUnitMapContext context) => inner.Map(xml, context);

    /// <inheritdoc />
    public JUnitMapResult Map(IReadOnlyList<ParseRecord> records, JUnitMapContext context) =>
        inner.Map(records, context);
}
