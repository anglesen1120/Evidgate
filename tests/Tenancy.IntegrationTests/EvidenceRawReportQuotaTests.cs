// The `raw_report_parse` write path's quota admission over the real Api composition, a real PostgreSQL and a real
// SeaweedFS container: the events one attempt inserted reserve the run's project counter inside the same transaction,
// and a block commits nothing at all while the refusal survives on the counter. The counters are seeded as the owner
// (a period's usage is an input of the case), and the handler is built from the production service collection so the
// gate, the block stamp and the clock are the registered ones.
using System.Globalization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// What one report attempt reserves before it meters: the events it inserted are one quota question against the run's
/// project counter (and the organization's), asked after the writes and before the commit, so a batch the billing
/// period has no room for commits nothing — no event, no receipt, no usage row, no report row and no run transition —
/// while a batch with room charges the counter exactly once.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceRawReportQuotaTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>The registry's producer kind for a pytest JUnit XML report.</summary>
    private const string ProducerKind = "pytest-junitxml";

    private const string XmlType = "application/xml";

    /// <summary>The warning line the gate's counters are seeded with.</summary>
    private const decimal WarnRatio = 0.8m;

    /// <summary>The outbox task type the gate enqueues when a counter first crosses its warning line.</summary>
    private const string WarningTask = "usage.quota.warn";

    /// <summary>The audited reason of an operator raise, the only transition that lifts a block.</summary>
    private const string RaiseReason = "the report lane needed room to admit the blocked batch again";

    /// <summary>A well-formed execution key: sha256 hex (00 §7), the shape <c>planned_execution</c> requires.</summary>
    private static readonly string ExecutionKey = new('a', 64);

    /// <summary>
    /// One case that resolves by its bare name (the seeded plan's case is named <c>probe</c>) and one that cannot, so
    /// an ordinary report inserts exactly one event and meters one unit.
    /// </summary>
    private static readonly byte[] OneEventBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="2">
            <testcase name="probe" time="0.012"/>
            <testcase classname="tests.test_payments" name="ghost_case" time="0.003"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>The same report extended with two planned cases, so one attempt inserts three events at once.</summary>
    private static readonly byte[] ThreeEventBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="3">
            <testcase name="probe" time="0.012"/>
            <testcase name="beta" time="0.004"/>
            <testcase name="gamma" time="0.008"/>
          </testsuite>
        </testsuites>
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
    public async Task A_Report_Under_The_Limit_Commits_And_Charges_The_Counter_Once()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 100m);

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "event_batch")).ShouldBe(1);
        (await CountOutboxAsync(world, UsageBatchAcceptedContract.TaskType)).ShouldBe(1);

        var counter = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        counter.Used.ShouldBe(1m, "the receipt meters one event, so the reservation is one unit");
        counter.BlockedAt.ShouldBeNull("a counter with room is never stamped");
        (await CountOutboxAsync(world, WarningTask)).ShouldBe(0, "one unit of a 100-unit limit is far below the warning line");
    }

    [Fact]
    public async Task A_Report_Crossing_The_Warning_Line_Still_Commits_And_Warns_Once()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 79m, limit: 100m);

        var outcome = await ParseAsync(world, artifactId);

        // Warn is an answer the caller proceeds on: the gate itself enqueued the one warning for this crossing.
        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "event_batch")).ShouldBe(1);
        (await CountOutboxAsync(world, WarningTask)).ShouldBe(1);

        var counter = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        counter.Used.ShouldBe(80m);
        counter.WarnedAt.ShouldNotBeNull("the crossing is stamped on the counter it crossed");
        counter.BlockedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_Report_The_Project_Cannot_Afford_Commits_Nothing_And_Stamps_The_Counter()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world, "beta", "gamma");
        var artifactId = await SeedArtifactAsync(world, ThreeEventBody);
        await SeedCounterAsync(counters, world, used: 9m, limit: 10m);

        var outcome = await ParseAsync(world, artifactId);

        // Limit 10, nine used, three inserted: the batch would land at 12, so the gate blocks it whole.
        outcome.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        outcome.Reason.ShouldBe("quota_exceeded");
        outcome.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        outcome.Events.ShouldBe(0);

        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        (await CountOutboxAsync(world, UsageBatchAcceptedContract.TaskType)).ShouldBe(0);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0, "the report row is the last insert, so a block leaves none");
        (await RunStateAsync(world)).ShouldBe("queued", "the queued -> running edge rolled back with the batch");
        (await CountOutboxAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(0);
        (await CountOutboxAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(0);
        (await CountAsync(world, "producer_slot")).ShouldBe(0);

        var counter = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        counter.Used.ShouldBe(9m, "a refused batch charges nothing");
        counter.BlockedAt.ShouldNotBeNull("the stamp written after the rollback is what an operator sees");
    }

    [Fact]
    public async Task A_Blocked_Report_Leaves_A_Running_Run_And_Its_Transition_Untouched()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await _fixture.ExecuteAsync(world.Database, $"update public.run set state = 'running' where id = '{world.RunId:D}'");
        var version = await RunVersionAsync(world);

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        (await RunStateAsync(world)).ShouldBe("running");
        (await RunVersionAsync(world)).ShouldBe(version, "the seeded update is the only version bump");
        (await CountOutboxAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(10m);
    }

    [Fact]
    public async Task A_Zero_Limit_Blocks_Every_Report()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 0m);

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldNotBeNull();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<World> StartAsync(Action<IServiceCollection>? services = null)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("quota-report"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("quota-project"));
        }

        var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database),
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-report-quota"),
            _store.Configuration(),
            services);

        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        await _fixture.ExecuteAsync(database, $"update public.run set state = 'queued' where id = '{runId:D}'");

        return new World(host, database, org, project, runId);
    }

    /// <summary>
    /// The run's plan: <c>probe</c> (the case <see cref="RunRowSeeder"/> seeds, resolved by its bare name) and one
    /// planned case per extra name, each with its own case, revision and execution key.
    /// </summary>
    private async Task SeedPlanAsync(World world, params string[] extraCases)
    {
        var revision = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.test_revision where org_id = '{world.Org:D}' and file_path = 'a'");

        await InsertPlannedAsync(world, ExecutionKey, revision);

        for (var index = 0; index < extraCases.Length; index++)
        {
            // Execution keys are sha256 hex, so the extra cases take the next letters of the alphabet that are hex.
            await InsertPlannedAsync(world, new string((char)('c' + index), 64), await SeedCaseAsync(world, extraCases[index]));
        }
    }

    private Task InsertPlannedAsync(World world, string executionKey, Guid revisionId) =>
        _fixture.ExecuteAsync(
            world.Database,
            "insert into public.planned_execution "
            + "(id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id) values "
            + $"('{Guid.CreateVersion7():D}', '{world.Org:D}', '{world.RunId:D}', '{executionKey}', "
            + $"'{revisionId:D}', 'sha256:{new string('b', 64)}', 'staging', 'shard-1')");

    /// <summary>One case of the world's project with its first revision, resolvable by its bare name.</summary>
    private async Task<Guid> SeedCaseAsync(World world, string name)
    {
        var caseId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var hash = "sha256:" + new string('c', 64);

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit) "
            + $"values ('{caseId:D}', '{world.Org:D}', '{world.Project:D}', '{Guid.CreateVersion7():D}', '{name}', "
            + "'playwright', 'abcdef0')");
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.test_revision (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, "
            + "content_sha256, assertion_hash) values "
            + $"('{revisionId:D}', '{world.Org:D}', '{caseId:D}', '{hash}', 'abcdef0', '{name}', 'playwright', "
            + $"'{hash}', '{hash}')");

        return revisionId;
    }

    /// <summary>
    /// One <c>ready</c> <c>raw_report</c> artifact whose final object the store really holds. The row may name a
    /// project other than the run's, which no key ties together, so a case can prove the pre-check asks about the run.
    /// </summary>
    /// <param name="world">The world the report belongs to.</param>
    /// <param name="body">The report's bytes.</param>
    /// <param name="projectId">The project the row names, or null for the run's own.</param>
    /// <returns>The artifact's id.</returns>
    private async Task<Guid> SeedArtifactAsync(World world, byte[] body, Guid? projectId = null)
    {
        var owner = projectId ?? world.Project;
        var artifactId = Guid.CreateVersion7();
        var key = ObjectKeyFor(world, owner, artifactId);
        var checksum = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(body));

        await _store.PutAsync(key, body);

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, "
            + "media_type, checksum, sensitivity, scan_state, state, bytes, failure_code) values "
            + $"('{artifactId:D}', '{world.Org:D}', '{owner:D}', '{world.RunId:D}', '{ExecutionKey}', 1, "
            + $"'{ArtifactKind.RawReport}', '{key}', '{XmlType}', '{checksum}', 'normal', 'pending', "
            + $"'{ArtifactState.Ready}', {body.LongLength.ToString(CultureInfo.InvariantCulture)}, null)");

        return artifactId;
    }

    /// <summary>The final key the artifact row mints, so a test writes and the handler reads the same object.</summary>
    private static string ObjectKeyFor(World world, Guid projectId, Guid artifactId) =>
        $"org/{world.Org:D}/project/{projectId:D}/run/{world.RunId:D}/attempt/{ExecutionKey}/1/{artifactId:D}";

    /// <summary>A second project of the world's org, so a counter of another project can be seeded beside the run's.</summary>
    /// <param name="world">The world the project belongs to.</param>
    /// <returns>The new project's id.</returns>
    private async Task<Guid> SeedOtherProjectAsync(World world)
    {
        var projectId = Guid.CreateVersion7();
        var slug = Unique.Slug("quota-other-project");

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.project (id, org_id, name, slug) "
            + $"values ('{projectId:D}', '{world.Org:D}', '{slug}', '{slug}')");

        return projectId;
    }

    /// <summary>A foreign tenant with one project, so a counter that must never block this one can be seeded.</summary>
    /// <param name="world">The world the foreign tenant is seeded beside.</param>
    /// <returns>The foreign org and project ids.</returns>
    private async Task<(Guid Org, Guid Project)> SeedForeignOrgAsync(World world)
    {
        var orgId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var slug = Unique.Slug("quota-foreign");

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.organization (id, name, slug) "
            + $"values ('{orgId:D}', '{slug}', '{slug}'); "
            + "insert into public.project (id, org_id, name, slug) "
            + $"values ('{projectId:D}', '{orgId:D}', '{slug}', '{slug}')");

        return (orgId, projectId);
    }

    /// <summary>The handler the task consumer builds, over the host's own registrations, minus the queue.</summary>
    /// <param name="world">The attempt's world.</param>
    /// <param name="log">Where the handler's lines go, or null for a silent one.</param>
    private static ParseRawReportHandler CreateHandler(
        World world,
        ILogger<ParseRawReportHandler>? log = null)
    {
        var services = world.Host.Services;

        return new ParseRawReportHandler(
            services.GetRequiredService<ITenantScopeFactory>(),
            services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IObjectStorage>(),
            services.GetRequiredService<IReportParserResolver>(),
            new EmulatedChildRunner(services.GetRequiredService<IParsing>()),
            services.GetRequiredService<IOptions<IngestionLimits>>(),
            services.GetServices<IJUnitMapper>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IOutboxWriter>(),
            services.GetRequiredService<IAuditTrail>(),
            services.GetRequiredService<TimeProvider>(),
            log ?? NullLogger<ParseRawReportHandler>.Instance);
    }

    private static async Task<ParseRawReportOutcome> ParseAsync(World world, Guid artifactId) =>
        await CreateHandler(world).ExecuteAsync(
            new ParseRawReportPayload(world.RunId, artifactId, ProducerKind),
            world.Org,
            Token);

    private Task<long> CountAsync(World world, string table, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.{table} where artifact_ids = array['{artifactId:D}'::uuid]");

    private Task<long> CountAsync(World world, string table) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.{table} where org_id = '{world.Org:D}'");

    private Task<long> CountOutboxAsync(World world, string taskType) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' and task_type = '{taskType}'");

    private Task<long> CountRawReportsAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.raw_report where org_id = '{world.Org:D}' and artifact_id = '{artifactId:D}'");

    private Task<string?> RunStateAsync(World world) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select state from public.run where id = '{world.RunId:D}'");

    private Task<int> RunVersionAsync(World world) =>
        _fixture.ScalarAsync<int>(
            world.Database,
            $"select version from public.run where id = '{world.RunId:D}'");
}
