// 05.3.2-T2.1/T2.2 against a real PostgreSQL: the reconciler runs inside the caller's tenant transaction, locks the run
// row FOR NO KEY UPDATE first (never FOR SHARE: a later revision insert re-takes that lock, so a share->update upgrade
// deadlocks), reads the planned shards from producer_slot and the observed events in ONE grouped aggregate over event,
// and reports the declared-versus-observed comparison. The reconciler half changes nothing, so its cases assert the run
// row and the (absent) evidence_revision row stay untouched; the T2.3/T2.4 cases below run the consumer's whole attempt
// (reconcile + freeze + revision insert + pointer move + transition) and assert that half instead.
//
// The comparison itself (every 00 §8.5 code from event evidence, the ordinal reason order, the open-shard short circuit,
// the count/end/contiguity bounds contracts/events/ordering.md §3 derives from the aggregate's min and max) is
// ReconcileComparerUnitTests.cs; this file is the database half. Test-name anchors come from 05.3.2 §9/§10
// (T2.1: ReconcileRunTask_ClaimedOnceUnderSkipLocked; T2.2: Reconcile_EachMissingReason_IncompleteWithCode) and the
// coordinator's S119 contract update (a test.started attempt with no test.finished, from any producer of the run, is
// unknown_status).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ReconcileRunTests
{
    /// <summary>The producer id of the seeded shard (05.2.1 registers <c>shard-N</c> per manifest shard).</summary>
    private const string Producer = "shard-1";

    /// <summary>The second seeded shard, for the cases whose evidence spans producers.</summary>
    private const string SecondProducer = "shard-2";

    /// <summary>One stored, acknowledged event of the run (the columns the aggregate reads).</summary>
    /// <param name="ProducerId">The producer (shard) that sent it.</param>
    /// <param name="Sequence">The producer's sequence; unique per <c>(org, run, producer)</c>.</param>
    /// <param name="Type">The event type, e.g. <c>test.finished</c>.</param>
    /// <param name="NormalizedStatus">The normalized status, or null for "the producer reported none".</param>
    /// <param name="ExecutionKey">The attempt's <c>execution_key</c> (<c>^[0-9a-f]{64}$</c>); derived when null.</param>
    /// <param name="AttemptNo">The attempt number (1-based).</param>
    /// <param name="Stale">The owner's-flag column (00 §9.4): set on the stale-evidence case, never read differently by the aggregate.</param>
    private sealed record StoredEvent(
        string ProducerId,
        long Sequence,
        string Type,
        string? NormalizedStatus,
        string? ExecutionKey = null,
        int AttemptNo = 1,
        bool Stale = false);

    private readonly PostgresTenancyFixture _fixture;

    public ReconcileRunTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ world and seeding

    /// <summary>
    /// One migrated database with an org, its project, a run and <paramref name="slots"/> producer slots.
    /// <paramref name="services"/> is the composition hook a case uses to replace one registered service (the freeze
    /// failure path); null leaves the production registrations alone.
    /// </summary>
    private Task<IngestTestWorld> StartAsync(int slots = 1, Action<IServiceCollection>? services = null) =>
        IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), slots: slots, services: services);

    /// <summary>
    /// The consumer tests need the host to carry the queue mechanism too: the production
    /// <c>AddEvidgateTasks</c> over the host's own app-role data source, with no hosted service, so nothing claims a row
    /// before the test drives the poll (the composition <c>IngestTraceCorrelationTests</c> uses). The reconciler is
    /// wrapped in <see cref="CountingRunReconciler"/> so a case can prove the handler body ran, not only that the task
    /// row ended <c>done</c>.
    /// </summary>
    private Task<IngestTestWorld> StartWithQueueAsync() =>
        IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            services: services =>
            {
                services.AddEvidgateTasks(new ConfigurationBuilder().Build());
                services.AddSingleton<ReconcileCallCounter>();
                services.RemoveAll<IRunReconciler>();
                services.AddScoped<IRunReconciler>(provider => new CountingRunReconciler(
                    new RunReconciler(
                        provider.GetRequiredService<ITenantDb>(),
                        provider.GetRequiredService<ITenantScopeAccessor>()),
                    provider.GetRequiredService<ReconcileCallCounter>()));
            });

    /// <summary>Counts how often a reconcile attempt really ran inside the host: the claim case's witness.</summary>
    private sealed class ReconcileCallCounter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Record() => Interlocked.Increment(ref _calls);
    }

    /// <summary>
    /// The production reconciler behind a call counter. A task row that ends <c>done</c> with one attempt says the
    /// consumer completed it; only the counter says the handler body — and so the reconcile — ran exactly once.
    /// </summary>
    private sealed class CountingRunReconciler(IRunReconciler inner, ReconcileCallCounter counter) : IRunReconciler
    {
        public async ValueTask<ReconcileOutcome> ReconcileAsync(Guid runId, CancellationToken cancellationToken)
        {
            counter.Record();

            return await inner.ReconcileAsync(runId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Moves a run to <paramref name="state"/> (the migration role: a case seeds states the routes cannot reach).</summary>
    private Task SetRunStateAsync(IngestTestWorld world, Guid runId, string state) =>
        _fixture.ExecuteAsync(world.Database, $"update public.run set state = '{state}' where id = '{runId:D}'");

    /// <summary>
    /// Closes one slot the way 04.1.2-T5's <c>CloseSlotAsync</c> writes it: <c>closed_at</c> plus the two declared values
    /// in one statement (0035's all-or-nothing check), so the reconciler sees a first-close declaration.
    /// </summary>
    private async Task CloseSlotAsync(
        IngestTestWorld world,
        Guid runId,
        string producer,
        long declaredLastSequence,
        int declaredCount)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await scope.ExecuteAsync(
            """
            update public.producer_slot
               set closed_at = now(), declared_last_sequence = @last_sequence, declared_count = @declared_count
             where org_id = @org_id and run_id = @run_id and producer_id = @producer_id
            """,
            ("last_sequence", declaredLastSequence),
            ("declared_count", declaredCount),
            ("org_id", world.Org),
            ("run_id", runId),
            ("producer_id", producer))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>Stores the events as the migration role (<c>event_owner_backfill</c> admits the owner, 0034).</summary>
    private async Task StoreEventsAsync(IngestTestWorld world, Guid runId, params StoredEvent[] events)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));

        foreach (var stored in events)
        {
            (await scope.ExecuteAsync(
                """
                insert into public.event
                  (id, org_id, run_id, event_id, producer_id, sequence, execution_key, attempt_no, type,
                   normalized_status, payload_hash, stale)
                values (@id, @org_id, @run_id, @event_id, @producer_id, @sequence, @execution_key, @attempt_no, @type,
                        @normalized_status, @payload_hash, @stale)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.Org),
                ("run_id", runId),
                ("event_id", Guid.CreateVersion7()),
                ("producer_id", stored.ProducerId),
                ("sequence", stored.Sequence),
                ("execution_key", stored.ExecutionKey ?? Digest(stored.ProducerId, stored.Sequence)),
                ("attempt_no", stored.AttemptNo),
                ("type", stored.Type),
                ("normalized_status", (object?)stored.NormalizedStatus ?? DBNull.Value),
                ("payload_hash", "sha256:" + new string('b', 64)),
                ("stale", stored.Stale))).ShouldBe(1);
        }

        await scope.CommitAsync();
    }

    /// <summary>
    /// Stores <paramref name="count"/> events of one producer in ONE statement (the volume the EXPLAIN case needs):
    /// the execution key is <c>md5(s) || md5(s+1)</c>, 64 lowercase hex characters as 0034's check demands.
    /// </summary>
    private async Task StoreBulkEventsAsync(IngestTestWorld world, Guid runId, long count)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await scope.ExecuteAsync(
            """
            insert into public.event
              (id, org_id, run_id, event_id, producer_id, sequence, execution_key, attempt_no, type,
               normalized_status, payload_hash)
            select gen_random_uuid(), @org_id, @run_id, gen_random_uuid(), @producer_id, s,
                   md5(s::text) || md5((s + 1)::text), 1, 'test.finished', 'passed',
                   'sha256:' || md5(s::text) || md5((s + 1)::text)
              from generate_series(1, @count::bigint) as s
            """,
            ("org_id", world.Org),
            ("run_id", runId),
            ("producer_id", Producer),
            ("count", count))).ShouldBe((int)count);
        await scope.CommitAsync();
    }

    /// <summary>A deterministic <c>execution_key</c> (<c>^[0-9a-f]{64}$</c>) for a producer's sequence.</summary>
    private static string Digest(string label, long sequence) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{label}|{sequence}"))));

    // ------------------------------------------------------------------ the reconciler, in the caller's tenant transaction

    /// <summary>One reconcile attempt in a fresh ambient tenant scope for the world's org, committed when it returns.</summary>
    private static async Task<ReconcileOutcome> ReconcileAsync(IngestTestWorld world, Guid runId)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var outcome = await services.ServiceProvider.GetRequiredService<IRunReconciler>().ReconcileAsync(runId, Token);

        await scope.CommitAsync();

        return outcome;
    }

    /// <summary>
    /// One full attempt the way the consumer runs it (05.3.2-T2.3/T2.4): reconcile, then — only for a compared attempt —
    /// freeze and persist the revision through <see cref="IReconcileFinisher"/>, all in ONE ambient tenant transaction,
    /// committed when both return. A rollback case uses <see cref="ReconcileRolledBackAsync"/> instead.
    /// </summary>
    private static Task<(ReconcileOutcome Outcome, ReconcileFinalization? Finalization)> ReconcileAndFinishAsync(
        IngestTestWorld world,
        Guid runId) =>
        ReconcileAndFinishCoreAsync(world.Api.Services, world, runId);

    /// <summary>
    /// The same attempt, resolved from the principal-carrying host instead of the API host. Only that container receives
    /// <see cref="IngestTestWorld.StartAsync"/>'s <c>services</c> hook (<c>IngestTestWorld</c> passes it to
    /// <c>TenancyApiFactory.WithPrincipal</c> alone), so a case that replaces a production service — the freeze — must run
    /// through here or the replacement never applies and the real service answers instead.
    /// </summary>
    private static Task<(ReconcileOutcome Outcome, ReconcileFinalization? Finalization)> ReconcileAndFinishOnHostAsync(
        IngestTestWorld world,
        Guid runId) =>
        ReconcileAndFinishCoreAsync(world.Host.Services, world, runId);

    private static async Task<(ReconcileOutcome Outcome, ReconcileFinalization? Finalization)> ReconcileAndFinishCoreAsync(
        IServiceProvider provider,
        IngestTestWorld world,
        Guid runId)
    {
        await using var services = provider.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var outcome = await services.ServiceProvider.GetRequiredService<IRunReconciler>().ReconcileAsync(runId, Token);
        ReconcileFinalization? finalization = null;
        if (outcome.Comparison is { } comparison)
        {
            finalization = await services.ServiceProvider.GetRequiredService<IReconcileFinisher>()
                .FinishAsync(runId, comparison, Token);
        }

        await scope.CommitAsync();

        return (outcome, finalization);
    }

    /// <summary>
    /// The same attempt, deliberately NOT committed: the scope is disposed, so the revision row, the pointer move and
    /// the state edge must all be gone (they share the attempt's one transaction).
    /// </summary>
    private static async Task<(ReconcileOutcome Outcome, ReconcileFinalization? Finalization)> ReconcileRolledBackAsync(
        IngestTestWorld world,
        Guid runId)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var outcome = await services.ServiceProvider.GetRequiredService<IRunReconciler>().ReconcileAsync(runId, Token);
        ReconcileFinalization? finalization = null;
        if (outcome.Comparison is { } comparison)
        {
            finalization = await services.ServiceProvider.GetRequiredService<IReconcileFinisher>()
                .FinishAsync(runId, comparison, Token);
        }

        return (outcome, finalization);
    }

    /// <summary>
    /// The persisted revision of a run as one delimited scalar (the suite's read style): id, <c>revision_no</c>,
    /// completeness, the missing codes, the manifest hash, the artifact-set hash ('' when null) and the watermark.
    /// </summary>
    /// <param name="world">The world's database.</param>
    /// <param name="runId">The run whose latest revision is read.</param>
    private static async Task<RevisionSummary> RevisionSummaryAsync(IngestTestWorld world, Guid runId)
    {
        var row = await world.ScalarAsync<string>(
            "select er.id::text || '|' || er.revision_no::text || '|' || er.completeness || '|' || "
            + "array_to_string(er.missing, ',') || '|' || er.manifest_hash || '|' || "
            + "coalesce(er.artifact_set_hash, '') || '|' || er.event_watermark::text "
            + $"from public.evidence_revision er where er.org_id = '{world.Org}' and er.run_id = '{runId:D}' "
            + "order by er.revision_no desc limit 1");

        var parts = row.Split('|');

        return new RevisionSummary(
            Guid.Parse(parts[0]),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            parts[2],
            parts[3].Length == 0 ? [] : parts[3].Split(','),
            parts[4],
            parts[5].Length == 0 ? null : parts[5],
            long.Parse(parts[6], CultureInfo.InvariantCulture));
    }

    /// <summary>One persisted <c>evidence_revision</c> row as the cases assert on it.</summary>
    /// <param name="Id">The revision id the run's pointer must name.</param>
    /// <param name="RevisionNo">The allocated revision number (1 for the first).</param>
    /// <param name="Completeness">The 00 §8.5 completeness.</param>
    /// <param name="Missing">The 00 §8.5 codes in stored order.</param>
    /// <param name="ManifestHash">The hash of the run's manifest.</param>
    /// <param name="ArtifactSetHash">The frozen artifact-set hash, or null when the row carries none.</param>
    /// <param name="EventWatermark">The stored-event count the revision froze.</param>
    private sealed record RevisionSummary(
        Guid Id,
        int RevisionNo,
        string Completeness,
        IReadOnlyList<string> Missing,
        string ManifestHash,
        string? ArtifactSetHash,
        long EventWatermark);

    /// <summary>The <c>manifest_hash</c> <see cref="PlannedExecutionSeeder"/> wrote for <paramref name="runId"/>'s manifest.</summary>
    private static string ManifestHashOf(Guid runId) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"manifest|{runId:D}")));

    /// <summary>The run's current evidence pointer, or null while no revision is frozen.</summary>
    private static Task<Guid?> PointerAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<Guid?>($"select current_evidence_revision_id from public.run where id = '{runId:D}'");

    /// <summary>
    /// One committed <c>ready</c> artifact of the run as the app role (0028's grants), with the final object key of its
    /// own identity. <c>attempt_id</c> stays null, so the pair resolves only if a <c>case_attempt</c> exists for
    /// <c>(execution_key, attempt_no)</c> — an execution nobody attempted leaves it an orphan (ruling T17).
    /// </summary>
    private async Task SeedReadyArtifactAsync(
        IngestTestWorld world,
        Guid runId,
        string executionKey,
        int attemptNo,
        string kind)
    {
        var id = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            """
            insert into public.artifact
              (id, org_id, project_id, run_id, execution_key, attempt_no, attempt_id, kind, object_key, media_type,
               checksum, sensitivity, scan_state, state)
            values (@id, @org_id, @project_id, @run_id, @execution_key, @attempt_no, null, @kind, @object_key,
                    'application/zip', @checksum, 'normal', 'clean', 'ready')
            """,
            ("id", id),
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("run_id", runId),
            ("execution_key", executionKey),
            ("attempt_no", attemptNo),
            ("kind", kind),
            ("object_key", $"org/{world.Org}/project/{world.Project}/run/{runId}/attempt/{executionKey}/{attemptNo}/{id}"),
            ("checksum", "sha256:" + id.ToString("N") + id.ToString("N")))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>
    /// One committed <c>active</c> policy revision of the world's project that requires <paramref name="requiredKind"/>
    /// (the schema's <c>required_artifacts</c> member) and pins the run to it: the requirement source the finisher reads
    /// through <c>IPolicyContracts</c> (D-S123-Q3). The migration role writes it, like every other seeded parent.
    /// </summary>
    private async Task<Guid> SeedPinnedPolicyAsync(IngestTestWorld world, Guid runId, string requiredKind)
    {
        var id = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await scope.ExecuteAsync(
            """
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, state, created_by)
            values (@id, @org_id, @project_id, 1, @hash, @body::jsonb, 'active', @created_by)
            """,
            ("id", id),
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("hash", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"policy|{runId:D}")))),
            ("body", $"{{\"required_artifacts\":[\"{requiredKind}\"]}}"),
            ("created_by", Guid.CreateVersion7()))).ShouldBe(1);
        await scope.CommitAsync();

        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set policy_revision_id = '{id:D}' where id = '{runId:D}'");

        return id;
    }

    /// <summary>A freeze that refuses the way 06.5.1's fail-closed contract does (a disagreeing <c>attempt_id</c>).</summary>
    private sealed class RefusingFreeze : IEvidenceFreeze
    {
        public Task<FreezeResult> FreezeAsync(FreezeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a stored attempt_id disagrees with the pair it resolves to");
    }

    // ------------------------------------------------------------------ reads (the migration role: one database per test)

    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    private static Task<int> VersionAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<int>($"select version from public.run where id = '{runId:D}'");

    private static Task<DateTime?> UpdatedAtAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<DateTime?>($"select updated_at from public.run where id = '{runId:D}'");

    private static Task<long> RevisionCountAsync(IngestTestWorld world) => world.CountAsync("evidence_revision");

    /// <summary>
    /// The <c>run.state_changed</c> audit rows of one run, written by 05.3.1's transition through the attempt's audit unit.
    /// The count is the proof that the audit row travels in the same transaction as the revision, the pointer and the
    /// state edge: one after a committed attempt, none after a rolled-back one.
    /// </summary>
    private static Task<long> StateChangeAuditCountAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            $"select count(*) from public.audit_log where org_id = '{world.Org}' "
            + $"and action = '{RunStateTransitionContract.AuditAction}' and object_type = 'run' "
            + $"and object_id = '{runId:D}' and actor_type = 'system'");

    /// <summary>The reasons of one outcome as <c>code:producer:detail</c>, for a readable failure message.</summary>
    private static string Describe(ReconcileOutcome outcome) =>
        outcome.Comparison is null
            ? $"disposition={outcome.Disposition}, no comparison"
            : $"disposition={outcome.Disposition}, missing=[{string.Join(" | ", outcome.Comparison.Missing.Select(
                reason => $"{reason.Code}:{reason.ProducerId}:{reason.Detail}"))}]";

    // ------------------------------------------------------------------ T2.2: matching evidence changes nothing

    /// <summary>
    /// A closed shard whose acknowledged events agree with its declaration has no missing reason, and the attempt leaves
    /// the run exactly as it was (<c>collecting</c>, same version) with no <c>evidence_revision</c> row: persisting a
    /// revision is 05.3.2-T2.4's, never T2.2's.
    /// </summary>
    [Fact]
    public async Task Reconcile_AllShardsMatch_NoMissing()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 3);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "failed"),
            new StoredEvent(Producer, 3, "test.finished", "skipped"));
        var version = await VersionAsync(world, runId);
        var updatedAt = await UpdatedAtAsync(world, runId);

        var outcome = await ReconcileAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared);
        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.ShouldBeEmpty(Describe(outcome));
        comparison.EventsMatch.ShouldBeTrue();
        comparison.ShardsPlanned.ShouldBe(1);
        comparison.ShardsClosed.ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("collecting", "the reconciler never moves the run");
        (await VersionAsync(world, runId)).ShouldBe(version);
        (await UpdatedAtAsync(world, runId)).ShouldBe(updatedAt);
        (await RevisionCountAsync(world)).ShouldBe(0, "T2.2 reports, T2.4 persists");
    }

    // ------------------------------------------------------------------ T2.2: the bounds a count cannot see

    /// <summary>
    /// A zero-based shard whose stored sequences are the contiguous range 0..2 agrees with a declaration of three events
    /// ending at sequence 2: <c>contracts/schemas/event-envelope.v1.json</c> allows <c>sequence >= 0</c>, so a shard that
    /// starts at 0 is contiguous, not a gap.
    /// </summary>
    [Fact]
    public async Task Reconcile_ZeroBasedContiguousShard_NoMissing()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 3);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 0, "test.finished", "passed"),
            new StoredEvent(Producer, 1, "test.finished", "failed"),
            new StoredEvent(Producer, 2, "test.finished", "skipped"));

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.ShouldBeEmpty(Describe(outcome));
        comparison.EventsMatch.ShouldBeTrue();
    }

    /// <summary>A one-based shard whose stored sequences are the contiguous range 1..5 agrees with a declaration of five events ending at 5.</summary>
    [Fact]
    public async Task Reconcile_OneBasedContiguousShard_NoMissing()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 5, declaredCount: 5);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "passed"),
            new StoredEvent(Producer, 3, "test.finished", "passed"),
            new StoredEvent(Producer, 4, "test.finished", "passed"),
            new StoredEvent(Producer, 5, "test.finished", "passed"));

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.ShouldBeEmpty(Describe(outcome));
        comparison.EventsMatch.ShouldBeTrue();
    }

    /// <summary>
    /// A closed slot that declared zero events matches exactly when nothing was stored: the count bound and the absence
    /// of any event agree, and an empty range has no end or contiguity to break.
    /// </summary>
    [Fact]
    public async Task Reconcile_ZeroDeclarationNoEvents_NoMissing()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 0, declaredCount: 0);

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.ShouldBeEmpty(Describe(outcome));
        comparison.EventsMatch.ShouldBeTrue();
    }

    /// <summary>
    /// An in-range event the owner's seed flagged <c>stale = true</c> is still an acknowledged event: every stored event
    /// counts toward the shard's declaration, so the shard's counts agree and no <c>count_mismatch</c> is named. What the
    /// flag means as evidence is <c>stale_epoch</c> (00 §8.5, 05.3.2-T2.3): a closed shard holding a stale event is
    /// incomplete with exactly that reason, and this half (T2.2) still writes nothing.
    /// </summary>
    [Fact]
    public async Task Reconcile_StaleEvent_CountsAgreeAndReportsStaleEpoch()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed", Stale: true),
            new StoredEvent(Producer, 2, "test.finished", "failed"));

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.Select(reason => reason.Code)
            .ShouldBe([ReconcileMissingCodes.StaleEpoch], $"{Describe(outcome)}: the count itself still agrees");
        comparison.EventsMatch.ShouldBeFalse();
        (await StateAsync(world, runId)).ShouldBe("collecting", "T2.2 reports, T2.4 persists");
        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ T2.2: every reason the events support

    /// <summary>
    /// One case per 00 §8.5 reason of the closed list the freeze and the comparison can name, with the code as the anchor
    /// (05.3.2 §2 AC5): a shard whose slot never closed (<c>missing_shard</c>); a closed shard whose stored events disagree
    /// with its declared pair in each of the ways the aggregate can see it — too few, a lower declared count than the events
    /// up to the high-water mark, events past the declared high-water mark, a hole with an equal count (declared three
    /// ending at 3, stored 0, 1, 2), a hole with the declared last sequence stored (declared five, stored 0, 1, 2, 3, 5),
    /// a gap the declared count also misses, and storage out of sequence order (the aggregate is order-insensitive, so only
    /// the bounds it carries decide); a closed shard with a status the producer never normalized (<c>unknown_status</c>); a
    /// closed shard whose <c>test.finished</c> says the setup was <c>blocked</c> (<c>setup_error</c>, 00 §8.3); a closed
    /// shard holding an in-range event flagged stale (<c>stale_epoch</c>); and an execution with a <c>ready</c> artifact but
    /// no <c>case_attempt</c> for its pair, which 06.5.1 reports as an orphan (<c>artifact_missing</c>, ruling T17).
    /// <para>
    /// Every case runs the consumer's whole attempt — reconcile, then the finisher's freeze, revision insert, pointer move
    /// and transition, committed — so each asserts the acceptance shape: one revision, <c>revision_no</c> 1, completeness
    /// <c>incomplete</c>, exactly that code in <c>missing[]</c>, the pointer on it and the run <c>incomplete</c>.
    /// </para>
    /// </summary>
    /// <param name="code">The 00 §8.5 code the revision must name.</param>
    /// <param name="scenario">Which shape of evidence <see cref="ArrangeEventEvidenceAsync"/> seeds.</param>
    [Theory]
    [InlineData(ReconcileMissingCodes.MissingShard, "open_shard")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "short")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "over")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "beyond_declared")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "gap_equal_count")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "gap_below_last")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "gap")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "out_of_order")]
    [InlineData(ReconcileMissingCodes.UnknownStatus, "null_status")]
    [InlineData(ReconcileMissingCodes.SetupError, "blocked_finish")]
    [InlineData(ReconcileMissingCodes.StaleEpoch, "stale_event")]
    [InlineData(ReconcileMissingCodes.ArtifactMissing, "orphan_artifact")]
    public async Task Reconcile_EachMissingReason_IncompleteWithCode(string code, string scenario)
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await ArrangeEventEvidenceAsync(world, runId, scenario);

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        var completed = finalization.ShouldNotBeNull("a compared attempt is always finished");
        completed.Completeness.ShouldBe(ReconcileFinisher.Incomplete, Describe(outcome));

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Completeness.ShouldBe(ReconcileFinisher.Incomplete, Describe(outcome));
        revision.Missing.ShouldBe([code], Describe(outcome));
        revision.RevisionNo.ShouldBe(1, "the first attempt of a run appends revision 1");

        (await StateAsync(world, runId)).ShouldBe("incomplete", "missing evidence never completes the run");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id);
        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ S119: unfinished started attempts

    /// <summary>
    /// A <c>test.started</c> attempt of a closed shard with no <c>test.finished</c> for the same
    /// <c>(execution_key, attempt_no)</c> is a status that was never reported: <c>unknown_status</c>, even though the
    /// declared count matches exactly (the count is not the only signal).
    /// </summary>
    [Fact]
    public async Task Reconcile_UnfinishedStarted_UnknownStatus()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.started", null, ExecutionKey: Digest("started", 1)),
            new StoredEvent(Producer, 2, "test.finished", "passed", ExecutionKey: Digest("finished", 2)));

        var outcome = await ReconcileAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.EventsMatch.ShouldBeFalse();
        comparison.Missing.Count.ShouldBe(1, Describe(outcome));
        comparison.Missing[0].Code.ShouldBe(ReconcileMissingCodes.UnknownStatus, Describe(outcome));
        comparison.Missing[0].ProducerId.ShouldBe(Producer);
    }

    /// <summary>A started attempt that finished under the same <c>(execution_key, attempt_no)</c> reported its status: no reason.</summary>
    [Fact]
    public async Task Reconcile_StartedAndFinished_NoMissing()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        var executionKey = Digest("attempt", 1);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.started", null, ExecutionKey: executionKey),
            new StoredEvent(Producer, 2, "test.finished", "passed", ExecutionKey: executionKey));

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.ShouldBeEmpty(Describe(outcome));
        comparison.EventsMatch.ShouldBeTrue();
    }

    /// <summary>
    /// A <c>test.finished</c> without a stored <c>test.started</c> (an adapter that reports only the outcome) is a
    /// reported status: no reason, and <c>unknown_status</c> stays about missing status, not missing pairing.
    /// </summary>
    [Fact]
    public async Task Reconcile_FinishedWithoutStarted_NoMissing()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.ShouldBeEmpty(Describe(outcome));
        comparison.EventsMatch.ShouldBeTrue();
    }

    /// <summary>
    /// An open shard is reported once as <c>missing_shard</c> and its events are not compared — not even an unfinished
    /// started attempt adds a second reason (the shard never declared anything to compare against).
    /// </summary>
    [Fact]
    public async Task Reconcile_OpenShardWithUnfinishedStarted_OnlyMissingShard()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.started", null));

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.EventsMatch.ShouldBeFalse();
        comparison.Missing.Count.ShouldBe(1, Describe(outcome));
        comparison.Missing[0].Code.ShouldBe(ReconcileMissingCodes.MissingShard, Describe(outcome));
        comparison.ShardsPlanned.ShouldBe(1);
        comparison.ShardsClosed.ShouldBe(0);
    }

    /// <summary>
    /// A <c>test.finished</c> reported by <b>another</b> producer of the same run clears the attempt: the status is the
    /// run's, not the shard's, so a started attempt that finished anywhere in the run reported its status and no
    /// <c>unknown_status</c> is named. The aggregate therefore looks the finish up across every producer of the
    /// <c>(org, run)</c>, not just the started row's own.
    /// </summary>
    [Fact]
    public async Task Reconcile_CrossProducerFinish_ClearsUnfinished()
    {
        await using var world = await StartAsync(slots: 2);
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        var executionKey = Digest("shared-attempt", 1);
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await CloseSlotAsync(world, runId, SecondProducer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.started", null, ExecutionKey: executionKey),
            new StoredEvent(SecondProducer, 1, "test.finished", "passed", ExecutionKey: executionKey));

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.ShouldBeEmpty(Describe(outcome));
        comparison.EventsMatch.ShouldBeTrue();
        comparison.ShardsPlanned.ShouldBe(2);
        comparison.ShardsClosed.ShouldBe(2);
    }

    /// <summary>
    /// Only <c>test.started</c> rows are unfinished-attempt candidates: an event of another type never becomes one, and a
    /// <c>test.finished</c> without its own started row is a reported status (no pairing required). One started attempt
    /// with no finish anywhere is counted exactly once — the detail's <c>unfinished=1</c> pins the aggregate's number,
    /// not just the presence of <c>unknown_status</c>.
    /// </summary>
    [Fact]
    public async Task Reconcile_UnfinishedStarted_CountsStartedRowsOnly()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 3);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.started", null, ExecutionKey: Digest("open", 1)),
            new StoredEvent(Producer, 2, "test.finished", "passed", ExecutionKey: Digest("closed", 2)),
            new StoredEvent(Producer, 3, "test.progress", null, ExecutionKey: Digest("other", 3)));

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.Count.ShouldBe(1, Describe(outcome));
        comparison.Missing[0].Code.ShouldBe(ReconcileMissingCodes.UnknownStatus, Describe(outcome));
        comparison.Missing[0].Detail.ShouldBe("null_status=0;unfinished=1");
    }

    /// <summary>The evidence shape of each theory row above, seeded into one run.</summary>
    /// <param name="world">The world whose run and slot are seeded.</param>
    /// <param name="runId">The run to seed for.</param>
    /// <param name="scenario">The shape name from the theory's <c>InlineData</c>.</param>
    private async Task ArrangeEventEvidenceAsync(IngestTestWorld world, Guid runId, string scenario)
    {
        switch (scenario)
        {
            // The slot never closed: the shard itself is missing, whatever its events say.
            case "open_shard":
                await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));
                break;

            // Declared five, stored three.
            case "short":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 5, declaredCount: 5);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 1, "test.finished", "passed"),
                    new StoredEvent(Producer, 2, "test.finished", "passed"),
                    new StoredEvent(Producer, 3, "test.finished", "passed"));
                break;

            // Declared two but three events up to the declared high-water mark.
            case "over":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 2);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 1, "test.finished", "passed"),
                    new StoredEvent(Producer, 2, "test.finished", "passed"),
                    new StoredEvent(Producer, 3, "test.finished", "passed"));
                break;

            // A fourth event past the declared high-water mark: accepted, so the declaration undercounts.
            case "beyond_declared":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 3);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 1, "test.finished", "passed"),
                    new StoredEvent(Producer, 2, "test.finished", "passed"),
                    new StoredEvent(Producer, 3, "test.finished", "passed"),
                    new StoredEvent(Producer, 4, "test.finished", "passed"));
                break;

            // Declared three events ending at 3, stored 0, 1 and 2: the count agrees and the hole where 3 belongs makes
            // the shard disagree, which a count-only comparison could not see.
            case "gap_equal_count":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 3);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 0, "test.finished", "passed"),
                    new StoredEvent(Producer, 1, "test.finished", "passed"),
                    new StoredEvent(Producer, 2, "test.finished", "passed"));
                break;

            // Declared five events ending at 5, stored 0, 1, 2, 3 and 5: the count and the end agree, 4 was never stored.
            case "gap_below_last":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 5, declaredCount: 5);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 0, "test.finished", "passed"),
                    new StoredEvent(Producer, 1, "test.finished", "passed"),
                    new StoredEvent(Producer, 2, "test.finished", "passed"),
                    new StoredEvent(Producer, 3, "test.finished", "passed"),
                    new StoredEvent(Producer, 5, "test.finished", "passed"));
                break;

            // Sequence 3 was never stored: a gap of one is a count disagreement.
            case "gap":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 4, declaredCount: 4);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 1, "test.finished", "passed"),
                    new StoredEvent(Producer, 2, "test.finished", "passed"),
                    new StoredEvent(Producer, 4, "test.finished", "passed"));
                break;

            // Stored out of sequence order (5, 1, 2) with two events missing: the aggregate is order-insensitive, so this
            // is the same count disagreement a gap of two is.
            case "out_of_order":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 5, declaredCount: 5);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 5, "test.finished", "passed"),
                    new StoredEvent(Producer, 1, "test.finished", "passed"),
                    new StoredEvent(Producer, 2, "test.finished", "passed"));
                break;

            // A closed shard whose counts agree but whose test.finished reports the setup as blocked (00 §8.3): the
            // dependants never ran, so the evidence is incomplete with setup_error (05.3.2-T2.3).
            case "blocked_finish":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 1, "test.finished", "blocked", ExecutionKey: Digest("blocked", 1)),
                    new StoredEvent(Producer, 2, "test.finished", "passed"));
                break;

            // A closed shard whose counts agree but which holds an in-range event the owner's seed flagged stale: the
            // evidence straddles an epoch change, so it is stale_epoch, never a count disagreement.
            case "stale_event":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 1, "test.finished", "passed", Stale: true),
                    new StoredEvent(Producer, 2, "test.finished", "passed"));
                break;

            // The events match, but a planned execution has a ready artifact whose (execution_key, attempt_no) has no
            // case_attempt: 06.5.1 reports the orphan (ruling T17), so the evidence is artifact_missing.
            case "orphan_artifact":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 1, "test.finished", "passed"),
                    new StoredEvent(Producer, 2, "test.finished", "passed"));
                await SeedReadyArtifactAsync(
                    world,
                    runId,
                    world.Plan.Executions[0].ExecutionKey,
                    attemptNo: 1,
                    kind: "trace");
                break;

            // Both events stored, one of them a test.finished whose normalized_status is null.
            default:
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(Producer, 1, "test.finished", null),
                    new StoredEvent(Producer, 2, "test.finished", "passed"));
                break;
        }
    }

    // ------------------------------------------------------------------ T2.1: only a collecting run is compared

    /// <summary>
    /// The trigger is not evidence that the run is collectable (05.3.2 §7): a run in any other state —
    /// <c>complete</c>, <c>incomplete</c>, <c>canceled</c> or still <c>running</c> — answers <c>NotCollectable</c>,
    /// compares nothing, writes no revision and leaves the state alone. The zero <c>evidence_revision</c> row is the
    /// half a retrying consumer would otherwise duplicate.
    /// </summary>
    /// <param name="state">The run state the reconcile trigger arrives too late for.</param>
    [Theory]
    [InlineData("complete")]
    [InlineData("incomplete")]
    [InlineData("canceled")]
    [InlineData("running")]
    public async Task Reconcile_NonCollectable_NotCompared(string state)
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, state);
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));
        var version = await VersionAsync(world, runId);

        var outcome = await ReconcileAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.NotCollectable, Describe(outcome));
        outcome.Comparison.ShouldBeNull("nothing is compared outside collecting");
        (await StateAsync(world, runId)).ShouldBe(state);
        (await VersionAsync(world, runId)).ShouldBe(version);
        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ T2.1: the tenant boundary

    /// <summary>An unknown run id is <c>RunNotFound</c>: the lock reads no row, so nothing is compared.</summary>
    [Fact]
    public async Task Reconcile_UnknownRunId_RunNotFound()
    {
        await using var world = await StartAsync();

        var outcome = await ReconcileAsync(world, Guid.CreateVersion7());

        outcome.Disposition.ShouldBe(ReconcileDisposition.RunNotFound, Describe(outcome));
        outcome.Comparison.ShouldBeNull();
        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// Tenant isolation: the run row of another org exists in the database, and this org's scope still sees nothing —
    /// `org_id = @org_id` plus RLS narrow the lock to zero rows, so the outcome is <c>RunNotFound</c> (no leak of
    /// existence, no cross-tenant read) and the foreign run is untouched.
    /// </summary>
    [Fact]
    public async Task Reconcile_RunInOtherTenant_RunNotFound()
    {
        await using var world = await StartAsync();
        var (otherOrg, _) = await world.CreateOtherOrgAsync("other");
        var foreignRunId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, otherOrg);
        await SetRunStateAsync(world, foreignRunId, "collecting");

        var outcome = await ReconcileAsync(world, foreignRunId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.RunNotFound, Describe(outcome));
        outcome.Comparison.ShouldBeNull();
        (await StateAsync(world, foreignRunId)).ShouldBe("collecting");
        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ T2.4's precondition: retrying changes nothing

    /// <summary>
    /// At-least-once delivery (A10) means the same trigger can run twice. Because T2.1/T2.2 never write, the second
    /// attempt returns an equal outcome and the run row is byte-identical (same version, same <c>updated_at</c>) with
    /// no <c>evidence_revision</c> row — the duplicate-revision failure T2.4's crash test forbids starts here.
    /// </summary>
    [Fact]
    public async Task Reconcile_RetrySafe_SameOutcomeNoRows()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 3);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "passed"));
        var version = await VersionAsync(world, runId);
        var updatedAt = await UpdatedAtAsync(world, runId);

        var first = await ReconcileAsync(world, runId);
        var second = await ReconcileAsync(world, runId);

        second.Disposition.ShouldBe(first.Disposition);
        second.Comparison.ShouldNotBeNull();
        second.Comparison!.ShardsPlanned.ShouldBe(first.Comparison!.ShardsPlanned);
        second.Comparison.ShardsClosed.ShouldBe(first.Comparison.ShardsClosed);
        second.Comparison.Missing
            .Select(reason => (reason.Code, reason.ProducerId, reason.Detail))
            .ToList()
            .ShouldBe(first.Comparison.Missing
                .Select(reason => (reason.Code, reason.ProducerId, reason.Detail))
                .ToList());
        (await StateAsync(world, runId)).ShouldBe("collecting");
        (await VersionAsync(world, runId)).ShouldBe(version);
        (await UpdatedAtAsync(world, runId)).ShouldBe(updatedAt);
        (await RevisionCountAsync(world)).ShouldBe(0, "a retry never writes a revision in T2.1/T2.2");
    }

    // ------------------------------------------------------------------ T2.1: the lock is FOR NO KEY UPDATE

    /// <summary>
    /// The lock the reconciler takes on the run row is <c>FOR NO KEY UPDATE</c>, in the order run → slot, because
    /// 05.3.2-T2.4's <c>evidence_revision</c> insert re-takes the run row for writing in the same transaction
    /// (05.3.2-T5 notes, S115): a <c>FOR SHARE</c> holder would sit waiting to upgrade behind the insert trigger's own
    /// write lock, the <c>40P01</c> upgrade deadlock.
    /// <para>
    /// The probe is the row-lock matrix, not a timing race: while the reconciler's transaction is still open (this test
    /// owns it, so the lock is held deterministically), <c>FOR KEY SHARE NOWAIT</c> from a second connection must
    /// succeed (compatible with every writer strength, so a <c>FOR UPDATE</c> holder is ruled out), while both
    /// <c>FOR SHARE NOWAIT</c> and <c>FOR NO KEY UPDATE NOWAIT</c> must fail <c>55P03</c> — a <c>FOR SHARE</c> or
    /// <c>FOR KEY SHARE</c> holder would have let one of them through. Only <c>FOR NO KEY UPDATE</c> satisfies all
    /// three. Each probe is its own transaction (an error aborts the one it ran in), and after the commit the same
    /// lock is free again.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reconcile_RunRowLockedForNoKeyUpdate()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var holding = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var outcome = await services.ServiceProvider.GetRequiredService<IRunReconciler>().ReconcileAsync(runId, Token);
        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));

        // KEY SHARE is compatible with FOR NO KEY UPDATE (and with FOR KEY SHARE, and with FOR SHARE): it must pass.
        await using (var probe = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await probe.ScalarAsync<string>(
                "select state from public.run where id = @id for key share nowait",
                ("id", runId))).ShouldBe("collecting", "FOR UPDATE would have refused a KEY SHARE probe");
        }

        // FOR SHARE conflicts with FOR NO KEY UPDATE and with FOR UPDATE, but not with FOR SHARE or FOR KEY SHARE.
        await using (var probe = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await probe.CaptureErrorAsync(
                "select state from public.run where id = @id for share nowait",
                ("id", runId))).SqlState.ShouldBe(
                    PostgresErrorCodes.LockNotAvailable,
                    "a FOR SHARE holder would have admitted this probe");
        }

        // FOR NO KEY UPDATE conflicts with FOR SHARE, FOR NO KEY UPDATE and FOR UPDATE.
        await using (var probe = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await probe.CaptureErrorAsync(
                "select state from public.run where id = @id for no key update nowait",
                ("id", runId))).SqlState.ShouldBe(
                    PostgresErrorCodes.LockNotAvailable,
                    "the reconciler must hold the run row at write strength, never FOR SHARE");
        }

        await holding.CommitAsync(TestContext.Current.CancellationToken);

        await using (var probe = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await probe.ScalarAsync<string>(
                "select state from public.run where id = @id for no key update nowait",
                ("id", runId))).ShouldBe("collecting", "the lock is released with the commit");
        }
    }

    // ------------------------------------------------------------------ T2.1: one statement per read, never per event

    /// <summary>
    /// The bounded statement contract: the lock plus state read, the planned-shard list and ONE grouped aggregate over
    /// <c>event</c> — never a round trip per event and never an <c>evidence_revision</c> insert. The count comes from the
    /// host's Npgsql command log (<c>TenancyApiFactory.Statements</c>, the statement-budget technique of
    /// <c>AuthoringCommandCounterTests</c> and <c>IngestEventBatchTests</c>), so it measures what the database was asked
    /// to do through the real registrations.
    /// </summary>
    [Fact]
    public async Task ReconcileRun_CommandCounter_Bounded()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 3);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "failed"),
            new StoredEvent(Producer, 3, "test.finished", "skipped"));

        world.Api.Statements.Clear();
        var outcome = await ReconcileAsync(world, runId);

        var statements = world.Api.Statements.Snapshot();
        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));

        var moduleStatements = statements
            .Where(statement => statement.Contains("public.", StringComparison.Ordinal))
            .ToList();
        moduleStatements.Count.ShouldBeLessThanOrEqualTo(4, string.Join(Environment.NewLine, statements));

        // One aggregate for the whole run: three events must not become three statements.
        statements.Count(statement => statement.Contains("public.event", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));

        statements.Any(statement => statement.Contains("public.evidence_revision", StringComparison.Ordinal))
            .ShouldBeFalse("T2.2 persists nothing (T2.4 owns the row)");
    }

    // ------------------------------------------------------------------ T2.1: the trigger is claimed exactly once

    /// <summary>The queue the outbox dispatcher routes an unconfigured task type to (01.5.1 §11).</summary>
    private const string ReconcileQueue = "default";

    /// <summary>
    /// T2.1's done-when anchor: the <c>orchestrator.run.reconcile</c> trigger is one task row, and four concurrent
    /// consumers over the real queue (a <c>FOR UPDATE SKIP LOCKED</c> claim each, on real PostgreSQL) hand it to exactly
    /// one of them — the other three poll empty — so the handler runs once, the row ends <c>done</c> with one attempt and
    /// its lease released, and a later poll finds nothing (a done row is never re-run).
    /// <para>
    /// The trigger is written through the production writer with 05.3.2-T1's own contract (<see
    /// cref="RunReconcileContract.PayloadJson"/> and <see cref="RunReconcileContract.IdempotencyKey"/>, the row a real
    /// finalize commits), and the handler the consumer resolves is 05.3.2-T2's real one: it is registered by
    /// <c>OrchestratorModule</c>, which the Api host composes, so <see cref="ITaskHandlerRegistry.TaskTypes"/> carries
    /// the type. Its reconcile leaves no row behind (T2.2 reports only), so the case asserts two things: the task row's
    /// own state and attempt count, and — through the <see cref="CountingRunReconciler"/> the queue host registers — that
    /// the handler body really ran once instead of a completion nobody executed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReconcileRunTask_ClaimedOnceUnderSkipLocked()
    {
        await using var world = await StartWithQueueAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 3);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "passed"),
            new StoredEvent(Producer, 3, "test.finished", "passed"));

        world.Host.Services.GetRequiredService<ITaskHandlerRegistry>().TaskTypes
            .ShouldContain(RunReconcileContract.OutboxTaskType, "the T2 handler must be registered for the type");

        var taskId = await world.Host.Services.GetRequiredService<ITaskQueueWriter>().EnqueueAsync(
            new TaskEnqueue
            {
                Queue = ReconcileQueue,
                TaskType = RunReconcileContract.OutboxTaskType,
                PayloadJson = RunReconcileContract.PayloadJson(runId, Producer, declaredLastSequence: 3, declaredCount: 3),
                IdempotencyKey = RunReconcileContract.IdempotencyKey(runId, Producer),
                OrgId = world.Org,
            },
            Token);
        taskId.ShouldNotBeNull("the reconcile trigger is one new task row");
        var reconcileTaskId = taskId.Value;

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();

        // Four polls start together (each runs to its first await as soon as it is made), so their claims really overlap
        // on one due row: exactly one wins, and only that one runs the handler.
        var polls = Enumerable.Range(0, 4).Select(_ => consumer.PollOnceAsync(ReconcileQueue, Token)).ToArray();
        var processed = await Task.WhenAll(polls);

        processed.Count(count => count == 1).ShouldBe(1, "exactly one poll claims and runs the reconcile row");
        processed.Count(count => count == 0).ShouldBe(3, "the losers of the claim skip the locked row");

        (await world.ScalarAsync<string>($"select state from public.task where id = '{reconcileTaskId:D}'"))
            .ShouldBe("done", "the handler completes the claimed row");
        (await world.ScalarAsync<int>($"select attempts from public.task where id = '{reconcileTaskId:D}'"))
            .ShouldBe(1, "one claim, one attempt: no double execution");
        (await world.ScalarAsync<bool>($"select locked_until is null from public.task where id = '{reconcileTaskId:D}'"))
            .ShouldBeTrue("the completion releases the lease");
        (await world.ScalarAsync<long>(
            $"select count(*) from public.task where idempotency_key = '{RunReconcileContract.IdempotencyKey(runId, Producer)}'"))
            .ShouldBe(1);
        (await consumer.PollOnceAsync(ReconcileQueue, Token)).ShouldBe(0, "a done row is never claimed again");

        // The handler now finishes the attempt (T2.3/T2.4): the matching evidence of this run yields ONE complete
        // revision, the pointer names it and the run left collecting — and however often the row were claimed it would
        // stay one revision, because a second attempt finds the run not collectable.
        (await RevisionCountAsync(world)).ShouldBe(1, "the claimed attempt freezes exactly one revision");
        var revision = await RevisionSummaryAsync(world, runId);
        revision.Completeness.ShouldBe(ReconcileFinisher.Complete);
        revision.RevisionNo.ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("complete");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id);

        // The row's own state says the consumer completed it once; the counter says the handler body — the reconcile —
        // really ran once, so a claim that never reached the handler cannot pass this case.
        world.Host.Services.GetRequiredService<ReconcileCallCounter>().Calls
            .ShouldBe(1, "the reconcile handler ran exactly once under the four concurrent claims");
    }

    // ------------------------------------------------------------------ T2.1: the aggregate uses the run's index

    /// <summary>
    /// The grouped aggregate is served by <c>event_run_producer_sequence_key</c> (0034: the unique
    /// <c>(org_id, run_id, producer_id, sequence)</c> key that exists instead of a second
    /// <c>(org_id, run_id)</c> index), so the reconciler never scans the whole event table.
    /// <para>
    /// The statement under test is <see cref="ReconcileSql.ObservedEvents"/> itself — the constant the reconciler ran a
    /// moment earlier in this case — with this run's parameters bound, so <c>EXPLAIN</c> plans exactly the production
    /// text (the extended protocol plans with the real values). The event table carries enough rows for the planner to
    /// choose on the plan alone (<c>ANALYZE</c> first, and no hint: <c>enable_seqscan</c> stays untouched).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reconcile_EventAggregate_UsesEventIndex()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 20, declaredCount: 20);
        await StoreBulkEventsAsync(world, runId, count: 20);

        // Volume: twenty further runs of the same tenant with 2500 events each, so this run's 20 rows are 0.04 % of the
        // table and the choice between the index and a sequential scan is made on the plan, not on a tiny table.
        for (var index = 0; index < 20; index++)
        {
            var otherRunId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);
            await IngestTestWorld.SeedSlotsAsync(_fixture, world.Database, world.Org, otherRunId, 1, boundJti: null);
            await StoreBulkEventsAsync(world, otherRunId, count: 2500);
        }

        await _fixture.ExecuteAsync(world.Database, "analyze public.event");
        await _fixture.ExecuteAsync(world.Database, "analyze public.producer_slot");

        var outcome = await ReconcileAsync(world, runId);
        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));

        var plan = string.Join(
            Environment.NewLine,
            await ExplainAsync(
                world,
                ReconcileSql.ObservedEvents,
                [("org_id", (object)world.Org), ("run_id", (object)runId)]));

        plan.ShouldContain("event_run_producer_sequence_key", customMessage: plan);
        Regex.IsMatch(plan, @"Seq Scan on (public\.)?event\b").ShouldBeFalse(plan);
    }

    // ------------------------------------------------------------------ T2.3/T2.4: AC4, the complete revision

    /// <summary>
    /// AC4, the whole attempt: every planned shard closed and matching, nothing required (the seeded run pinned no policy,
    /// so the requirement set is legitimately empty) and no orphan artifact — the finisher appends revision 1 with
    /// completeness <c>complete</c>, an empty <c>missing[]</c>, the run's manifest hash, the frozen artifact-set hash and
    /// the event watermark, moves the run's pointer to that revision and moves the run to <c>complete</c>.
    /// </summary>
    [Fact]
    public async Task Reconcile_AllShardsMatch_WritesCompleteRevision()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 3);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "failed"),
            new StoredEvent(Producer, 3, "test.finished", "skipped"));

        (await world.ScalarAsync<string>(
            $"select coalesce(policy_revision_id::text, '') from public.run where id = '{runId:D}'"))
            .ShouldBeEmpty("the seeded run pinned no policy: nothing is required and the evidence alone decides");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        outcome.Comparison!.Missing.ShouldBeEmpty(Describe(outcome));
        var completed = finalization.ShouldNotBeNull();
        completed.Completeness.ShouldBe(ReconcileFinisher.Complete);
        completed.RevisionNo.ShouldBe(1);

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Id.ShouldBe(completed.RevisionId);
        revision.RevisionNo.ShouldBe(1);
        revision.Completeness.ShouldBe("complete");
        revision.Missing.ShouldBeEmpty();
        revision.ManifestHash.ShouldBe(ManifestHashOf(runId), "the revision records the run's own manifest hash");
        revision.ArtifactSetHash.ShouldNotBeNullOrWhiteSpace("the frozen set hash is recorded even for an empty set");
        revision.EventWatermark.ShouldBe(3, "the watermark is every stored event of the run");

        (await StateAsync(world, runId)).ShouldBe("complete");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id, "the pointer names the revision just appended");
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await StateChangeAuditCountAsync(world, runId))
            .ShouldBe(1, "the state edge is audited once, in the attempt's transaction");
    }

    /// <summary>
    /// Fail closed on the empty plan: a run with no planned shard proves nothing, so it is <c>incomplete</c> even though no
    /// reason applies (0049 allows an incomplete row with an empty <c>missing[]</c>; only <c>complete</c> is forbidden
    /// without evidence).
    /// </summary>
    [Fact]
    public async Task Reconcile_ZeroPlannedShards_IncompleteWithoutReasons()
    {
        await using var world = await StartAsync(slots: 0);
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        outcome.Comparison!.ShardsPlanned.ShouldBe(0);
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Completeness.ShouldBe("incomplete");
        revision.Missing.ShouldBeEmpty("no code applies, but nothing was proven");
        (await StateAsync(world, runId)).ShouldBe("incomplete");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id);
    }

    /// <summary>
    /// The <c>missing[]</c> order is stable and two-part: the comparison's codes first (producer id, then code ordinal) and
    /// the freeze's own reasons after them, in the order <see cref="FreezeResult.Missing"/> gives. One stale event names
    /// <c>stale_epoch</c> and one orphan artifact names <c>artifact_missing</c>; the stored array is exactly that order.
    /// </summary>
    [Fact]
    public async Task Reconcile_MissingCodes_ComparisonThenFreeze()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed", Stale: true),
            new StoredEvent(Producer, 2, "test.finished", "passed"));
        await SeedReadyArtifactAsync(world, runId, world.Plan.Executions[0].ExecutionKey, attemptNo: 1, kind: "trace");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Missing.ShouldBe(
            [ReconcileMissingCodes.StaleEpoch, ReconcileMissingCodes.ArtifactMissing],
            Describe(outcome));
    }

    /// <summary>
    /// The revision, the pointer move and the state edge share the attempt's one transaction: an attempt whose consumer
    /// never commits leaves no revision row, no pointer and a run still <c>collecting</c>, so a retry starts clean instead
    /// of finding half a revision (00 §5.5 C1/C7, the fail-closed rule of STATUS.md:588/589).
    /// </summary>
    [Fact]
    public async Task Reconcile_RolledBackAttempt_LeavesNoRevisionAndRunCollecting()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));

        var (outcome, finalization) = await ReconcileRolledBackAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull("the attempt wrote its revision inside the transaction that then rolled back");
        (await RevisionCountAsync(world)).ShouldBe(0, "an uncommitted attempt leaves no revision behind");
        (await PointerAsync(world, runId)).ShouldBeNull("the pointer moves in the same transaction as the insert");
        (await StateAsync(world, runId)).ShouldBe("collecting");
        (await StateChangeAuditCountAsync(world, runId))
            .ShouldBe(0, "the state edge's audit row shares the transaction and rolls back with it");
    }

    /// <summary>
    /// A freeze that refuses (06.5.1's fail-closed contract: a disagreeing <c>attempt_id</c>, a short resolution
    /// <c>UPDATE</c> or a run above <see cref="FreezeRequest.MaxReadyArtifacts"/>) propagates as
    /// <see cref="InvalidOperationException"/>: the consumer's transaction rolls back, so no revision and no state change
    /// survive and the retry is the recovery.
    /// </summary>
    [Fact]
    public async Task Reconcile_FreezeRefused_RollsBackAndWritesNoRevision()
    {
        await using var world = await StartAsync(services: services =>
        {
            services.RemoveAll<IEvidenceFreeze>();
            services.AddScoped<IEvidenceFreeze, RefusingFreeze>();
        });
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await ReconcileAndFinishOnHostAsync(world, runId));

        (await RevisionCountAsync(world)).ShouldBe(0);
        (await PointerAsync(world, runId)).ShouldBeNull();
        (await StateAsync(world, runId)).ShouldBe("collecting");
        (await StateChangeAuditCountAsync(world, runId))
            .ShouldBe(0, "the refused attempt rolls its audit row back with the revision");
    }

    /// <summary>
    /// At-least-once delivery (A10) never duplicates a revision: the first committed attempt appends revision 1 and moves
    /// the run to <c>complete</c>, so the second trigger for the same run answers <c>NotCollectable</c>, reaches no
    /// finisher and leaves the run and the single revision byte-identical.
    /// </summary>
    [Fact]
    public async Task Reconcile_AfterCommit_SecondTriggerNotCollectableAndNoSecondRevision()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "passed"));

        var first = await ReconcileAndFinishAsync(world, runId);
        first.Finalization.ShouldNotBeNull().RevisionNo.ShouldBe(1);
        var version = await VersionAsync(world, runId);

        var second = await ReconcileAndFinishAsync(world, runId);

        second.Outcome.Disposition.ShouldBe(ReconcileDisposition.NotCollectable, Describe(second.Outcome));
        second.Finalization.ShouldBeNull("a non-collecting run is never frozen");
        (await RevisionCountAsync(world)).ShouldBe(1, "a retry never appends a second revision");
        (await RevisionSummaryAsync(world, runId)).RevisionNo.ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("complete");
        (await VersionAsync(world, runId)).ShouldBe(version, "nothing was written by the second attempt");
    }

    /// <summary>
    /// Tenant isolation on the finishing path: tenant A's scope reconciling tenant B's run id answers
    /// <c>RunNotFound</c> before any comparison, so no finisher runs, no revision is written and B's run stays
    /// <c>collecting</c> with no pointer (the lock read is <c>org_id</c>-filtered and RLS-scoped).
    /// </summary>
    [Fact]
    public async Task Reconcile_OtherTenantRun_WritesNoRevision()
    {
        await using var world = await StartAsync();
        var (otherOrg, _) = await world.CreateOtherOrgAsync("other");
        var foreignRunId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, otherOrg);
        await SetRunStateAsync(world, foreignRunId, "collecting");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, foreignRunId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.RunNotFound, Describe(outcome));
        finalization.ShouldBeNull();
        (await RevisionCountAsync(world)).ShouldBe(0);
        (await StateAsync(world, foreignRunId)).ShouldBe("collecting");
        (await PointerAsync(world, foreignRunId)).ShouldBeNull();
    }

    /// <summary>
    /// The requirement source of D-S123-Q3: the run pins an <c>active</c> policy revision whose body requires the
    /// <c>trace</c> artifact kind, so every planned execution of the run is required to have one. The event evidence
    /// itself matches and there is no artifact at all, so the freeze reports one unmet requirement per planned execution —
    /// both mapped to <c>artifact_missing</c> — and the run ends <c>incomplete</c>.
    /// </summary>
    [Fact]
    public async Task Reconcile_PolicyRequiredKindMissing_IncompleteWithArtifactMissing()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "passed"));
        await SeedPinnedPolicyAsync(world, runId, requiredKind: "trace");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        outcome.Comparison!.Missing.ShouldBeEmpty("the event evidence itself agrees");
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Completeness.ShouldBe("incomplete");
        revision.Missing.ShouldBe(
            [ReconcileMissingCodes.ArtifactMissing, ReconcileMissingCodes.ArtifactMissing],
            "one unmet requirement per planned execution");
        (await StateAsync(world, runId)).ShouldBe("incomplete");
    }

    // ------------------------------------------------------------------ T2.4: bounded statements

    /// <summary>
    /// The finishing half is bounded too: one insert into <c>evidence_revision</c> (whose <c>revision_no</c> is allocated
    /// in the statement), one pointer update and the transition's one state update — never a statement per requirement or
    /// per artifact. The count comes from the host's Npgsql command log, the technique
    /// <see cref="ReconcileRun_CommandCounter_Bounded"/> uses.
    /// </summary>
    [Fact]
    public async Task ReconcileFinish_CommandCounter_Bounded()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "failed"));

        world.Api.Statements.Clear();
        var (outcome, _) = await ReconcileAndFinishAsync(world, runId);

        var statements = world.Api.Statements.Snapshot();
        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));

        var moduleStatements = statements
            .Where(statement => statement.Contains("public.", StringComparison.Ordinal))
            .ToList();
        moduleStatements.Count.ShouldBeLessThanOrEqualTo(16, string.Join(Environment.NewLine, statements));

        statements.Count(statement => statement.Contains("insert into public.evidence_revision", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.StartsWith("update public.run", StringComparison.Ordinal))
            .ShouldBe(2, $"the pointer move and the state edge: {string.Join(Environment.NewLine, statements)}");
        statements.Any(statement => statement.Contains("insert into public.artifact", StringComparison.Ordinal))
            .ShouldBeFalse("a freeze never writes an artifact row");
    }

    /// <summary>Runs <c>EXPLAIN (FORMAT TEXT)</c> over one statement with its parameters bound, as the migration role.</summary>
    /// <param name="world">The world's database.</param>
    /// <param name="sql">The statement text, with its <c>@name</c> placeholders.</param>
    /// <param name="parameters">The values the placeholders stand for.</param>
    private async Task<IReadOnlyList<string>> ExplainAsync(
        IngestTestWorld world,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var command = connection.CreateCommand();
        command.CommandText = "explain (format text) " + sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(Token);
        var lines = new List<string>();
        while (await reader.ReadAsync(Token))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }
}
