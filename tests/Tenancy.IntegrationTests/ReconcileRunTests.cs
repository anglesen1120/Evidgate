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
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application.Freeze;
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

[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed partial class ReconcileRunTests
{
    /// <summary>The producer id of the seeded shard (05.2.1 registers <c>shard-N</c> per manifest shard).</summary>
    private const string Producer = "shard-1";

    /// <summary>The second seeded shard, for the cases whose evidence spans producers.</summary>
    private const string SecondProducer = "shard-2";

    /// <summary>The third seeded shard, for the pending rule's three-shard sequence.</summary>
    private const string ThirdProducer = "shard-3";

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
    /// <para>
    /// Unless <paramref name="attempts"/> is false, every planned execution of the seeded run records one terminal
    /// attempt (the migration role: <c>case_attempt</c> is FORCE RLS), so a case decides completeness from the evidence it
    /// arranges and not from the plan alone: the reconciler names a planned execution with no recorded attempt as
    /// <c>missing_result</c>, so a world whose plan answered nothing would add that reason to every case here.
    /// </para>
    /// </summary>
    private async Task<IngestTestWorld> StartAsync(
        int slots = 1,
        Action<IServiceCollection>? services = null,
        bool attempts = true,
        string? database = null)
    {
        var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), slots: slots, services: services, database: database);

        return attempts ? await AnswerPlanOrDisposeAsync(world) : world;
    }

    /// <summary>
    /// The consumer tests need the host to carry the queue mechanism too: the production
    /// <c>AddEvidgateTasks</c> over the host's own app-role data source, with no hosted service, so nothing claims a row
    /// before the test drives the poll (the composition <c>IngestTraceCorrelationTests</c> uses). The reconciler is
    /// wrapped in <see cref="CountingRunReconciler"/> so a case can prove the handler body ran, not only that the task
    /// row ended <c>done</c>.
    /// </summary>
    private async Task<IngestTestWorld> StartWithQueueAsync(int attemptsSkip = 0, int slots = 1, string? database = null)
    {
        var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            slots: slots,
            database: database,
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
        await AnswerPlanOrDisposeAsync(world, attemptsSkip);

        return world;
    }

    /// <summary>
    /// Answers the seeded plan and returns the same world. A failure here disposes the world before the error
    /// escapes, because the caller's <c>await using</c> never binds a world a factory did not return.
    /// </summary>
    private static async Task<IngestTestWorld> AnswerPlanOrDisposeAsync(IngestTestWorld world, int skip = 0)
    {
        try
        {
            await RecordPlanAttemptsAsync(world, skip);
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Records one terminal attempt per planned execution of the seeded run, through the real writer (so the schema owns
    /// the row shape and resolves each case): the plan row of a case that reported a status, which is what the
    /// reconciler's coverage read looks for.
    /// </summary>
    /// <param name="world">The world whose plan is answered.</param>
    /// <param name="skip">How many trailing planned executions stay unattempted; zero answers the whole plan.</param>
    private static async Task RecordPlanAttemptsAsync(IngestTestWorld world, int skip = 0)
    {
        var executions = world.Plan.Executions;
        skip.ShouldBeLessThanOrEqualTo(executions.Count, "the plan must keep at least one answered execution");
        if (executions.Count == skip)
        {
            return;
        }

        await using var attempts = await AttemptScope.BeginAsync(world.Api, world.Org);
        var inputs = executions
            .Take(executions.Count - skip)
            .Select(execution => new CaseAttemptInput(execution.ExecutionKey, 1, "passed"))
            .ToArray();

        var outcomes = await attempts.WriteAsync(world.RunId, inputs);
        outcomes.Count.ShouldBe(inputs.Length);
        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        await attempts.CommitAsync();
    }

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
        public async ValueTask<ReconcileOutcome> ReconcileAsync(
            Guid runId,
            bool lateTrigger,
            bool reprocessTrigger,
            CancellationToken cancellationToken)
        {
            counter.Record();

            return await inner.ReconcileAsync(runId, lateTrigger, reprocessTrigger, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Moves a run to <paramref name="state"/> (the migration role: a case seeds states the routes cannot reach).</summary>
    private Task SetRunStateAsync(IngestTestWorld world, Guid runId, string state) =>
        _fixture.ExecuteAsync(world.Database, $"update public.run set state = '{state}' where id = '{runId:D}'");

    /// <summary>
    /// Moves the run's collection deadline into the past (the migration role, on a column no route moves). The deadline is
    /// decided by the run-lock statement (<c>coalesce(r.collection_deadline_at &lt;= now(), false)</c>), so this column is
    /// the only clock a case has to set: with it in the past an open shard is <c>missing_shard</c> and the attempt
    /// finishes, where the seeded future deadline makes the attempt wait (<see cref="ReconcileDisposition.Pending"/>).
    /// </summary>
    private Task PassCollectionDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() - interval '1 second' where id = '{runId:D}'");

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

    /// <summary>
    /// One reconcile attempt in a fresh ambient tenant scope for the world's org, committed when it returns.
    /// <paramref name="lateTrigger"/> and <paramref name="reprocessTrigger"/> are what the outbox payload carries: only a
    /// late-evidence trigger may compare a run that already finished, and only a reprocess trigger may compare a canceled
    /// one.
    /// </summary>
    private static async Task<ReconcileOutcome> ReconcileAsync(
        IngestTestWorld world,
        Guid runId,
        bool lateTrigger = false,
        bool reprocessTrigger = false)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var outcome = await services.ServiceProvider.GetRequiredService<IRunReconciler>()
            .ReconcileAsync(runId, lateTrigger, reprocessTrigger, Token);

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
        Guid runId,
        bool lateTrigger = false,
        bool reprocessTrigger = false) =>
        ReconcileAndFinishCoreAsync(world.Api.Services, world, runId, lateTrigger, reprocessTrigger);

    /// <summary>
    /// The same attempt, resolved from the principal-carrying host instead of the API host. Only that container receives
    /// <see cref="IngestTestWorld.StartAsync"/>'s <c>services</c> hook (<c>IngestTestWorld</c> passes it to
    /// <c>TenancyApiFactory.WithPrincipal</c> alone), so a case that replaces a production service — the freeze — must run
    /// through here or the replacement never applies and the real service answers instead.
    /// </summary>
    private static Task<(ReconcileOutcome Outcome, ReconcileFinalization? Finalization)> ReconcileAndFinishOnHostAsync(
        IngestTestWorld world,
        Guid runId,
        bool lateTrigger = false,
        bool reprocessTrigger = false) =>
        ReconcileAndFinishCoreAsync(world.Host.Services, world, runId, lateTrigger, reprocessTrigger);

    private static async Task<(ReconcileOutcome Outcome, ReconcileFinalization? Finalization)> ReconcileAndFinishCoreAsync(
        IServiceProvider provider,
        IngestTestWorld world,
        Guid runId,
        bool lateTrigger,
        bool reprocessTrigger)
    {
        await using var services = provider.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var outcome = await services.ServiceProvider.GetRequiredService<IRunReconciler>()
            .ReconcileAsync(runId, lateTrigger, reprocessTrigger, Token);
        ReconcileFinalization? finalization = null;
        if (outcome.Disposition is ReconcileDisposition.Compared or ReconcileDisposition.ComparedLate
                or ReconcileDisposition.ComparedReprocess
            && outcome.Comparison is { } comparison)
        {
            finalization = await services.ServiceProvider.GetRequiredService<IReconcileFinisher>()
                .FinishAsync(runId, comparison, Settlement(outcome), Token);
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
        Guid runId,
        bool lateTrigger = false)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var outcome = await services.ServiceProvider.GetRequiredService<IRunReconciler>()
            .ReconcileAsync(runId, lateTrigger, reprocessTrigger: false, Token);
        ReconcileFinalization? finalization = null;
        if (outcome.Disposition is ReconcileDisposition.Compared or ReconcileDisposition.ComparedLate
                or ReconcileDisposition.ComparedReprocess
            && outcome.Comparison is { } comparison)
        {
            finalization = await services.ServiceProvider.GetRequiredService<IReconcileFinisher>()
                .FinishAsync(runId, comparison, Settlement(outcome), Token);
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

    /// <summary>
    /// The production freeze behind a call counter: the pending rule's witness that a waiting attempt never freezes
    /// (a <c>Pending</c> outcome that reached the finisher would show up as a call), and the unsatisfiable case's witness
    /// that the freeze is asked for an empty requirement list.
    /// </summary>
    private sealed class CountingFreeze(IEvidenceFreeze inner, FreezeCallCounter counter) : IEvidenceFreeze
    {
        public async Task<FreezeResult> FreezeAsync(FreezeRequest request, CancellationToken cancellationToken)
        {
            counter.Record(request);

            return await inner.FreezeAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How often the freeze ran, and how many requirements the last call carried.</summary>
    private sealed class FreezeCallCounter
    {
        private int _calls;
        private int _lastRequiredCount = -1;

        public int Calls => Volatile.Read(ref _calls);

        public int LastRequiredCount => Volatile.Read(ref _lastRequiredCount);

        public void Record(FreezeRequest request)
        {
            Interlocked.Increment(ref _calls);
            Volatile.Write(ref _lastRequiredCount, request.Required.Count);
        }
    }

    /// <summary>
    /// The host with the production freeze behind a <see cref="CountingFreeze"/>: the only way to count freeze calls,
    /// because the <c>services</c> hook of <see cref="IngestTestWorld.StartAsync"/> reaches the principal-carrying host
    /// alone (which is why the cases below run through <see cref="ReconcileAndFinishOnHostAsync"/>).
    /// </summary>
    private static void CountFreezeCalls(IServiceCollection services, FreezeCallCounter counter)
    {
        services.AddSingleton(counter);
        services.RemoveAll<IEvidenceFreeze>();
        services.AddScoped<IEvidenceFreeze>(provider => new CountingFreeze(
            new EvidenceFreeze(
                provider.GetRequiredService<ITenantDb>(),
                provider.GetRequiredService<ITenantScopeAccessor>()),
            counter));
    }

    /// <summary>A requirement seam with a fixed answer, so the finisher's unsatisfiable branch is exercised directly.</summary>
    private sealed class FixedRequirements(RequiredSet set) : IRequiredArtifactResolver
    {
        public ValueTask<RequiredSet> ResolveAsync(Guid runId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(set);
    }

    // ------------------------------------------------------------------ reads (the migration role: one database per test)

    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    private static Task<int> VersionAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<int>($"select version from public.run where id = '{runId:D}'");

    private static Task<DateTime?> UpdatedAtAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<DateTime?>($"select updated_at from public.run where id = '{runId:D}'");

    private static Task<long> RevisionCountAsync(IngestTestWorld world) =>
        world.CountAsync("evidence_revision", $"org_id = '{world.Org:D}'");

    /// <summary>Revisions of one run, whatever org it belongs to (the cross-tenant cases assert on a foreign run).</summary>
    private static Task<long> RevisionCountOfAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync("evidence_revision", $"run_id = '{runId:D}'");

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

    /// <summary>
    /// The settlement the consumer derives from an outcome: a late or reprocess comparison appends a revision to a run
    /// that already finished, and an overdue comparison is a forced close, so neither leaves the decision to the
    /// collecting edge.
    /// </summary>
    private static ReconcileSettlement Settlement(ReconcileOutcome outcome) => outcome.Disposition switch
    {
        ReconcileDisposition.ComparedLate => ReconcileSettlement.AppendLateEvidence,
        ReconcileDisposition.ComparedReprocess => ReconcileSettlement.AppendReprocessEvidence,
        ReconcileDisposition.ComparedOverdue => ReconcileSettlement.CloseOverdue,
        _ => ReconcileSettlement.DecideCollection,
    };

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
    /// An in-range event carrying <c>stale = true</c> is an acknowledged event like any other, and it names no reason:
    /// nothing in the product writes that flag — no producer's current epoch is stored anywhere to compare an event's
    /// epoch against — so a reason built on it could only ever fire for a row an operator backfilled by hand, and the
    /// comparison would then claim a check it does not make. The flag stays in the row and in the 00 §8.5 code list; the
    /// reason returns with lease epochs, together with the writer that sets it.
    /// </summary>
    [Fact]
    public async Task Reconcile_StaleFlag_NamesNoReasonAndLeavesTheShardDecidedByItsCounts()
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

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.ShouldBeEmpty($"{Describe(outcome)}: the flag names nothing, the counts agree");
        comparison.EventsMatch.ShouldBeTrue();
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
    /// closed shard whose <c>test.finished</c> says the setup was <c>blocked</c> (<c>setup_error</c>, 00 §8.3); and an
    /// execution with a <c>ready</c> artifact but no <c>case_attempt</c> for its pair, which the freeze reports as an orphan
    /// (<c>artifact_missing</c>).
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
    [InlineData(ReconcileMissingCodes.ArtifactMissing, "orphan_artifact")]
    [InlineData(ReconcileMissingCodes.MissingResult, "uncovered_execution")]
    public async Task Reconcile_EachMissingReason_IncompleteWithCode(string code, string scenario)
    {
        // The coverage scenario is the one shape the plan rows themselves decide, so its world records no attempt up
        // front; every other shape keeps the whole plan answered and adds one event-evidence or freeze reason.
        await using var world = await StartAsync(attempts: scenario != "uncovered_execution");
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
    /// started attempt adds a second reason (the shard never declared anything to compare against). The deadline has
    /// passed here, so the attempt decides instead of waiting; the next case is the same run before the deadline.
    /// </summary>
    [Fact]
    public async Task Reconcile_OpenShardWithUnfinishedStarted_OnlyMissingShard()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await PassCollectionDeadlineAsync(world, runId);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.started", null));

        var outcome = await ReconcileAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.EventsMatch.ShouldBeFalse();
        comparison.Missing.Count.ShouldBe(1, Describe(outcome));
        comparison.Missing[0].Code.ShouldBe(ReconcileMissingCodes.MissingShard, Describe(outcome));
        comparison.ShardsPlanned.ShouldBe(1);
        comparison.ShardsClosed.ShouldBe(0);
    }

    /// <summary>
    /// The pending rule at the reconciler (COORDINATOR-DECISION S123, F1): the same open shard <b>before</b> the
    /// collection deadline is not a verdict — the attempt answers <see cref="ReconcileDisposition.Pending"/> and still
    /// carries the comparison it made (its counts are the log line), so the run keeps collecting until the shard closes
    /// or the deadline forces the decision.
    /// </summary>
    [Fact]
    public async Task Reconcile_OpenShardBeforeDeadline_Pending()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.started", null));

        var outcome = await ReconcileAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Pending, Describe(outcome));
        var comparison = outcome.Comparison.ShouldNotBeNull("a pending attempt still reports what it compared");
        comparison.Missing[0].Code.ShouldBe(ReconcileMissingCodes.MissingShard, Describe(outcome));
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
            // The slot never closed and the deadline has passed: the shard itself is missing, whatever its events say.
            // (Before the deadline the attempt would wait instead — see the pending-rule case below.)
            case "open_shard":
                await PassCollectionDeadlineAsync(world, runId);
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

            // The events match, but a planned execution has a ready artifact whose (execution_key, attempt_no) has no
            // case_attempt: 06.5.1 reports the orphan (ruling T17), so the evidence is artifact_missing. The pair is
            // attempt 2 because attempt 1 is the terminal attempt the world records for this execution.
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
                    attemptNo: 2,
                    kind: "trace");
                break;

            // Every producer closed with matching events, and one planned execution recorded no attempt at all: the plan
            // says a case never reported, which no event and no count can show (missing_result, plan coverage).
            case "uncovered_execution":
                await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
                await StoreEventsAsync(
                    world,
                    runId,
                    new StoredEvent(
                        Producer,
                        1,
                        "test.finished",
                        "passed",
                        ExecutionKey: world.Plan.Executions[0].ExecutionKey));
                await RecordPlanAttemptsAsync(world, skip: 1);
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

    // ------------------------------------------------------------------ T2.1: a run outside its deadline is not compared

    /// <summary>
    /// The trigger is not evidence that the run is collectable (05.3.2 §7): a run that cannot take evidence —
    /// <c>complete</c>, <c>canceled</c>, or still <c>running</c> inside its collection deadline — answers
    /// <c>NotCollectable</c>, compares nothing, writes no revision and leaves the state alone (past that deadline the
    /// same run is force-closed instead: <c>ReconcileForcedIncompleteTests</c>). The zero <c>evidence_revision</c> row is
    /// the half a retrying consumer would otherwise duplicate. <c>incomplete</c> is here too, for an ordinary trigger:
    /// only a trigger that carries late evidence compares a finished run, and only inside its window
    /// (<see cref="Reconcile_IncompleteRun_WithLateTrigger_ComparesLate"/>).
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

    // ------------------------------------------------------------------ an incomplete run compares late evidence

    /// <summary>
    /// A run that finished <c>incomplete</c> still takes evidence inside its late window, and that evidence only counts
    /// through a revision of its own: the late trigger asks for exactly this comparison. So the state that used to be
    /// <c>NotCollectable</c> now compares — <c>ComparedLate</c> — and the consumer settles it as an append, never as a
    /// second collection decision. The reconciler half still writes nothing: no revision, no state move, no version bump.
    /// </summary>
    [Fact]
    public async Task Reconcile_IncompleteRun_WithLateTrigger_ComparesLate()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "incomplete");
        await OpenLateWindowAsync(world, runId);
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));
        var version = await VersionAsync(world, runId);

        (await LateWindowOpenAsync(world, runId)).ShouldBeTrue("the window the late evidence was accepted in");

        var outcome = await ReconcileAsync(world, runId, lateTrigger: true);

        outcome.Disposition.ShouldBe(ReconcileDisposition.ComparedLate, Describe(outcome));
        outcome.Comparison.ShouldNotBeNull("a finished run compares the evidence its late window took");
        (await StateAsync(world, runId)).ShouldBe("incomplete", "the reconciler half never decides the run again");
        (await VersionAsync(world, runId)).ShouldBe(version);
        (await RevisionCountAsync(world)).ShouldBe(0, "persisting the late revision is the finisher's half");
    }

    /// <summary>
    /// An ordinary trigger — a finalize's close, a deadline sweep, a redelivery of either — names no late evidence, so it
    /// must not compare a run that already finished, even while the late window happens to be open: nothing new arrived
    /// for it to count, and a revision appended there would claim evidence the run never took.
    /// </summary>
    [Fact]
    public async Task Reconcile_IncompleteRun_WithOrdinaryTrigger_IsNotCollectable()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "incomplete");
        await OpenLateWindowAsync(world, runId);
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));

        (await LateWindowOpenAsync(world, runId)).ShouldBeTrue(
            "the window is open, so the trigger's marker is what keeps the run uncompared");

        var outcome = await ReconcileAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.NotCollectable, Describe(outcome));
        outcome.Comparison.ShouldBeNull("no late evidence arrived, so there is nothing to compare");
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
        var (otherOrg, _) = await world.CreateOtherOrgAsync(Unique.Slug("other"));
        var foreignRunId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, otherOrg);
        await SetRunStateAsync(world, foreignRunId, "collecting");

        var outcome = await ReconcileAsync(world, foreignRunId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.RunNotFound, Describe(outcome));
        outcome.Comparison.ShouldBeNull();
        (await StateAsync(world, foreignRunId)).ShouldBe("collecting");
        (await RevisionCountOfAsync(world, foreignRunId)).ShouldBe(0);
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
        var outcome = await services.ServiceProvider.GetRequiredService<IRunReconciler>()
            .ReconcileAsync(runId, lateTrigger: false, reprocessTrigger: false, Token);
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

    /// <summary>
    /// A waiting attempt stops before the coverage read: the plan's uncovered executions cannot turn a wait into a
    /// verdict, so the bounded read under the run lock is spent only on the attempt that compares for real.
    /// </summary>
    [Fact]
    public async Task Reconcile_OpenShardBeforeDeadline_SkipsTheCoverageRead()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");

        world.Api.Statements.Clear();
        var outcome = await ReconcileAsync(world, runId);

        var statements = world.Api.Statements.Snapshot();
        outcome.Disposition.ShouldBe(ReconcileDisposition.Pending, Describe(outcome));
        outcome.Comparison.ShouldNotBeNull("a pending attempt still reports the evidence it compared");
        // The log proves capture is on before the negative check means anything: a snapshot that logged nothing would
        // satisfy "no planned_execution statement" without the attempt ever running.
        statements.ShouldContain(
            statement => statement.Contains("public.producer_slot", StringComparison.Ordinal),
            "the waiting attempt still reads the slots it decided on");
        statements.Any(statement => statement.Contains("public.planned_execution", StringComparison.Ordinal))
            .ShouldBeFalse(string.Join(Environment.NewLine, statements));
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
        // Four concurrent claims over one due row need a database where this is the only due row.
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(database: owned.Name);
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
        // The plan is decided by this case's own row counts, so it owns a database: a shared one carries other
        // tenants' rows and the ANALYZE below would plan against them.
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database: owned);
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
        (await world.ScalarAsync<long>(
            $"select count(*) from public.run_state_event where run_id = '{runId:D}' "
            + "and from_state = 'collecting' and to_state = 'complete'"))
            .ShouldBe(1, "the finisher's own edge appends its history row in the attempt's transaction (migration 0082)");
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
    /// the freeze's own reasons after them, in the order <see cref="FreezeResult.Missing"/> gives. One normalized-status
    /// hole names <c>unknown_status</c> and one orphan artifact names <c>artifact_missing</c>; the stored array is exactly
    /// that order.
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
            new StoredEvent(Producer, 1, "test.step", null),
            new StoredEvent(Producer, 2, "test.finished", null));
        await SeedReadyArtifactAsync(world, runId, world.Plan.Executions[0].ExecutionKey, attemptNo: 2, kind: "trace");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Missing.ShouldBe(
            [ReconcileMissingCodes.UnknownStatus, ReconcileMissingCodes.ArtifactMissing],
            Describe(outcome));
    }

    /// <summary>
    /// The revision, the pointer move and the state edge share the attempt's one transaction: an attempt whose consumer
    /// never commits leaves no revision row, no pointer and a run still <c>collecting</c>, so a retry starts clean instead
    /// of finding half a revision (00 §5.5 C1/C7, the fail-closed rule of docs/status-carried.md:116/117).
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
        var (otherOrg, _) = await world.CreateOtherOrgAsync(Unique.Slug("other"));
        var foreignRunId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, otherOrg);
        await SetRunStateAsync(world, foreignRunId, "collecting");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, foreignRunId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.RunNotFound, Describe(outcome));
        finalization.ShouldBeNull();
        (await RevisionCountOfAsync(world, foreignRunId)).ShouldBe(0);
        (await StateAsync(world, foreignRunId)).ShouldBe("collecting");
        (await PointerAsync(world, foreignRunId)).ShouldBeNull();
    }

    /// <summary>
    /// The requirement source of D-S123-Q3: the run pins an <c>active</c> policy revision whose body requires the
    /// <c>trace</c> artifact kind, so every planned execution of the run is required to have one. The event evidence
    /// itself matches and there is no artifact at all, so the freeze reports one unmet requirement per planned execution —
    /// both mapped to <c>artifact_missing</c>, which the revision stores <b>once</b> (the codes are de-duplicated,
    /// PerfRev P3) — and the run ends <c>incomplete</c>.
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
            [ReconcileMissingCodes.ArtifactMissing],
            "one code names the gap however many executions it applies to");
        (await StateAsync(world, runId)).ShouldBe("incomplete");
    }

    /// <summary>
    /// A pinned policy may require a kind outside the closed artifact-kind list: the body schema states only a
    /// <c>lower_snake</c> pattern (<c>contracts/schemas/policy-revision.v1.json</c>) and the contract read forwards the
    /// token, while the closed list lives inside the Evidence module. The freeze refuses such a set by shape — its
    /// <c>Validate</c> runs before the first statement, so the attempt's transaction is untouched — and the finisher
    /// then re-freezes with no requirements and records the one <c>artifact_missing</c> marker: the revision is
    /// <c>incomplete</c> and the run leaves <c>collecting</c> instead of the attempt being retried until the queue
    /// dead-letters it (SecRev F1/F2).
    /// </summary>
    [Fact]
    public async Task Reconcile_PolicyRequiresUnknownKind_IncompleteWithArtifactMissing()
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
        await SeedPinnedPolicyAsync(world, runId, requiredKind: "not_a_kind");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        outcome.Comparison!.Missing.ShouldBeEmpty("the event evidence itself agrees");
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Completeness.ShouldBe("incomplete");
        revision.Missing.ShouldBe(
            [ReconcileMissingCodes.ArtifactMissing],
            "a requirement the freeze refuses by shape is recorded once, not retried");
        (await StateAsync(world, runId)).ShouldBe("incomplete");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id);
        (await RevisionCountAsync(world)).ShouldBe(1, "the refused attempt persists exactly one revision");
    }

    // ------------------------------------------------------------------ the pending rule (F1)

    /// <summary>
    /// The pending rule, three shards at a time (COORDINATOR-DECISION S123, F1): after the first finalize and after the
    /// second, a planned shard is still open and the collection deadline is still ahead, so each attempt answers
    /// <see cref="ReconcileDisposition.Pending"/> and finishes <b>nothing</b> — the run stays <c>collecting</c> with no
    /// revision, no pointer move, no audit row, and the freeze is never called. The third finalize closes the last slot,
    /// the evidence matches, and the attempt decides <c>complete</c>: one freeze, one revision, one pointer, one audit
    /// edge.
    /// </summary>
    [Fact]
    public async Task Reconcile_OpenShardsBeforeDeadline_PendingUntilLastShardCloses()
    {
        var counter = new FreezeCallCounter();
        await using var world = await StartAsync(slots: 3, services: services => CountFreezeCalls(services, counter));
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");

        await CloseShardAsync(world, runId, Producer);
        var first = await ReconcileAndFinishOnHostAsync(world, runId);
        await AssertWaitingAsync(world, runId, first, counter);

        await CloseShardAsync(world, runId, SecondProducer);
        var second = await ReconcileAndFinishOnHostAsync(world, runId);
        await AssertWaitingAsync(world, runId, second, counter);

        await CloseShardAsync(world, runId, ThirdProducer);
        var third = await ReconcileAndFinishOnHostAsync(world, runId);

        third.Outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(third.Outcome));
        third.Outcome.Comparison!.Missing.ShouldBeEmpty(Describe(third.Outcome));
        third.Finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Complete);
        counter.Calls.ShouldBe(1, "only the deciding attempt freezes the evidence");

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Completeness.ShouldBe(ReconcileFinisher.Complete);
        revision.RevisionNo.ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("complete");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id);
        (await RevisionCountAsync(world)).ShouldBe(1, "the two pending attempts appended nothing");
        (await StateChangeAuditCountAsync(world, runId)).ShouldBe(1);
    }

    /// <summary>
    /// The complete verdict is reachable from a pending attempt: the shard that made the first attempt wait closes and
    /// the next trigger decides <c>complete</c> on the matching evidence — one revision, the pointer on it, the run out of
    /// <c>collecting</c>.
    /// </summary>
    [Fact]
    public async Task Reconcile_OpenShardBeforeDeadline_ThenClosingShardCompletes()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");

        var waiting = await ReconcileAndFinishAsync(world, runId);

        waiting.Outcome.Disposition.ShouldBe(ReconcileDisposition.Pending, Describe(waiting.Outcome));
        waiting.Finalization.ShouldBeNull("a pending attempt never reaches the finisher");
        (await StateAsync(world, runId)).ShouldBe("collecting", "the run keeps collecting while a shard is open");
        (await RevisionCountAsync(world)).ShouldBe(0);
        (await PointerAsync(world, runId)).ShouldBeNull();
        (await StateChangeAuditCountAsync(world, runId)).ShouldBe(0, "a pending attempt writes no audit row");

        await CloseShardAsync(world, runId, Producer);
        var deciding = await ReconcileAndFinishAsync(world, runId);

        deciding.Outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(deciding.Outcome));
        deciding.Finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Complete);
        var revision = await RevisionSummaryAsync(world, runId);
        revision.RevisionNo.ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("complete");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id);
    }

    /// <summary>
    /// An open shard past the collection deadline is decided, not waited on: <c>missing_shard</c> makes the revision
    /// <c>incomplete</c> and moves the run out of <c>collecting</c> (the verdict 05.3.2-T3.1 forces when no shard ever
    /// closes). The deadline comes from the database clock in the run-lock statement, so the case only moves the column.
    /// </summary>
    [Fact]
    public async Task Reconcile_OpenShardPastDeadline_IncompleteWithMissingShard()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await PassCollectionDeadlineAsync(world, runId);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Completeness.ShouldBe("incomplete");
        revision.Missing.ShouldBe([ReconcileMissingCodes.MissingShard], Describe(outcome));
        (await StateAsync(world, runId)).ShouldBe("incomplete");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id);
    }

    /// <summary>
    /// Two attempts for the same run, started together after its last shard closed: the run-row lock serialises them, so
    /// exactly one compares and finishes (<c>complete</c>, revision 1) and the other, whose lock statement re-reads the
    /// committed row, answers <c>NotCollectable</c> — one revision however many consumers race (at-least-once, A10).
    /// </summary>
    [Fact]
    public async Task Reconcile_TwoConcurrentAttempts_AppendExactlyOneRevision()
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

        // Both attempts start now, in their own execution flows: each opens its own tenant scope (a second scope in this
        // flow would be refused as "already open", NpgsqlTenantScopeFactory), and they meet on the run row's lock.
        var attempts = await Task.WhenAll(
            Detached(() => ReconcileAndFinishAsync(world, runId), Token),
            Detached(() => ReconcileAndFinishAsync(world, runId), Token));

        attempts.Count(attempt => attempt.Finalization is not null)
            .ShouldBe(1, "the two attempts serialise on the run row: one decides, the other arrives after the verdict");
        attempts.Single(attempt => attempt.Finalization is null).Outcome.Disposition
            .ShouldBe(ReconcileDisposition.NotCollectable, "the loser finds the run out of collecting");
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await RevisionSummaryAsync(world, runId)).Completeness.ShouldBe(ReconcileFinisher.Complete);
        (await StateAsync(world, runId)).ShouldBe("complete");
        (await StateChangeAuditCountAsync(world, runId)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ required-set bounds (PerfRev P1, P3)

    /// <summary>
    /// An unsatisfiable requirement set (the plan times the policy exceeds <see cref="FreezeRequest.MaxRequired"/>) is not
    /// retried forever and never truncated into a shorter list that could look satisfied: the finisher freezes with the
    /// empty spec list it was given and records <b>one</b> <c>artifact_missing</c> after the freeze's own reasons, so the
    /// revision is <c>incomplete</c> and the run leaves <c>collecting</c>.
    /// </summary>
    [Fact]
    public async Task Reconcile_UnsatisfiableRequirementSet_IncompleteWithArtifactMissing()
    {
        var counter = new FreezeCallCounter();
        await using var world = await StartAsync(services: services =>
        {
            CountFreezeCalls(services, counter);
            services.RemoveAll<IRequiredArtifactResolver>();
            services.AddScoped<IRequiredArtifactResolver>(
                _ => new FixedRequirements(new RequiredSet([], Unsatisfiable: true)));
        });
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "passed"));

        var (outcome, finalization) = await ReconcileAndFinishOnHostAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);
        counter.Calls.ShouldBe(1, "the attempt still freezes, with the specs it has");
        counter.LastRequiredCount.ShouldBe(0, "an over-bound set is never truncated into a requirement list");

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Completeness.ShouldBe("incomplete");
        revision.Missing.ShouldBe([ReconcileMissingCodes.ArtifactMissing], Describe(outcome));
        (await StateAsync(world, runId)).ShouldBe("incomplete");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id);
    }

    /// <summary>
    /// Two orphan artifacts (each a <c>MissingReason</c> of the freeze) name the same code: the revision stores
    /// <c>artifact_missing</c> once, keeping the first-seen order of every reason the attempt named, while the comparison
    /// the reconciler reports keeps its own per-producer detail (PerfRev P3).
    /// </summary>
    [Fact]
    public async Task Reconcile_DuplicateMissingCodes_StoredOnce()
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
        await SeedReadyArtifactAsync(world, runId, Digest("orphan", 1), attemptNo: 1, kind: "trace");
        await SeedReadyArtifactAsync(world, runId, Digest("orphan", 2), attemptNo: 1, kind: "trace");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Comparison!.Missing.ShouldBeEmpty("the event evidence agrees; the orphans come from the freeze");
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Missing.ShouldBe(
            [ReconcileMissingCodes.ArtifactMissing],
            "two orphan artifacts are one code in the revision");
    }

    /// <summary>
    /// Closes one shard and stores the two matching events it declared (the pending rule's per-shard step, i.e. what one
    /// finalize plus its batch commits).
    /// </summary>
    /// <param name="world">The world whose slots are seeded.</param>
    /// <param name="runId">The run to close the shard of.</param>
    /// <param name="producer">The producer id of the shard.</param>
    private async Task CloseShardAsync(IngestTestWorld world, Guid runId, string producer)
    {
        await CloseSlotAsync(world, runId, producer, declaredLastSequence: 2, declaredCount: 2);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(producer, 1, "test.finished", "passed"),
            new StoredEvent(producer, 2, "test.finished", "passed"));
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a fresh execution flow: <see cref="ExecutionContext.SuppressFlow"/> keeps this
    /// flow's ambient tenant scope (an <c>AsyncLocal</c>) out of the child, so a racing attempt opens its own scope
    /// instead of being refused a second one here (the pattern of <c>RunStateTransitionTests.Detached</c>).
    /// </summary>
    /// <typeparam name="T">The result of the work.</typeparam>
    /// <param name="work">The work to run in its own flow.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private static Task<T> Detached<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, cancellationToken);
        }
    }

    /// <summary>
    /// Asserts a waiting attempt: <c>Pending</c> with its comparison, no finisher, no freeze call, and a run left exactly
    /// as it was — <c>collecting</c>, no revision, no pointer, no audit row.
    /// </summary>
    /// <param name="world">The world the attempt ran against.</param>
    /// <param name="runId">The run of the attempt.</param>
    /// <param name="attempt">The attempt's outcome and (absent) finalization.</param>
    /// <param name="counter">The freeze call counter of the host.</param>
    private static async Task AssertWaitingAsync(
        IngestTestWorld world,
        Guid runId,
        (ReconcileOutcome Outcome, ReconcileFinalization? Finalization) attempt,
        FreezeCallCounter counter)
    {
        attempt.Outcome.Disposition.ShouldBe(ReconcileDisposition.Pending, Describe(attempt.Outcome));
        attempt.Outcome.Comparison.ShouldNotBeNull("a pending attempt still reports what it compared");
        attempt.Finalization.ShouldBeNull("a pending attempt never reaches the finisher");
        counter.Calls.ShouldBe(0, "a pending attempt must not freeze the evidence");
        (await StateAsync(world, runId)).ShouldBe("collecting", "the run keeps collecting while a shard is open");
        (await RevisionCountAsync(world)).ShouldBe(0);
        (await PointerAsync(world, runId)).ShouldBeNull();
        (await StateChangeAuditCountAsync(world, runId)).ShouldBe(0, "a pending attempt writes no audit row");
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

    /// <summary>
    /// A pinned policy's trace requirement is satisfied for every planned execution by a ready artifact that resolves
    /// to its committed case attempt. Matching closed-shard evidence then produces a complete revision (05.3.2 AC4).
    /// The freeze-call counter proves the requirement set was really applied — one requirement per planned execution,
    /// so the complete verdict cannot be reached by an empty set — and that the freeze ran exactly once (no retry).
    /// </summary>
    [Fact]
    public async Task Reconcile_PolicyRequiredKindReady_WritesCompleteRevision()
    {
        var counter = new FreezeCallCounter();
        // attempts: false - this case seeds the plan's attempts itself (the freeze resolves each requirement through a
        // case_attempt), so a second write of the same pair would come back a duplicate, not the acceptance it asserts.
        await using var world = await StartAsync(attempts: false, services: services => CountFreezeCalls(services, counter));
        var runId = world.RunId;
        var executions = world.Plan.Executions;
        executions.ShouldNotBeEmpty("the positive requirement case must exercise a non-empty plan");
        await SetRunStateAsync(world, runId, "collecting");
        await SeedPinnedPolicyAsync(world, runId, requiredKind: "trace");

        // Follow EvidenceFreezeGuardTests.SeedAttemptsAsync: the real writer owns the schema and resolves each case.
        await using (var attempts = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            var inputs = executions.Select(execution => new CaseAttemptInput(execution.ExecutionKey, 1, "passed")).ToArray();
            var outcomes = await attempts.WriteAsync(runId, inputs);
            outcomes.Count.ShouldBe(executions.Count);
            outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
            await attempts.CommitAsync();
        }

        foreach (var execution in executions)
        {
            await SeedReadyArtifactAsync(world, runId, execution.ExecutionKey, attemptNo: 1, kind: "trace");
        }

        var events = executions
            .Select((execution, index) => new StoredEvent(
                Producer, index + 1L, "test.finished", "passed", ExecutionKey: execution.ExecutionKey))
            .ToArray();
        await StoreEventsAsync(world, runId, events);
        await CloseSlotAsync(
            world, runId, Producer, declaredLastSequence: events.Length, declaredCount: events.Length);

        var (outcome, finalization) = await ReconcileAndFinishOnHostAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        outcome.Comparison!.Missing.ShouldBeEmpty("every closed shard's stored evidence matches");
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Complete);

        // One required kind times every planned execution: the set the finisher froze with, asked once.
        counter.Calls.ShouldBe(1, "a satisfied requirement set freezes once and is never refused by shape");
        counter.LastRequiredCount.ShouldBe(executions.Count, "the policy's kind was required for every planned execution");

        var revision = await RevisionSummaryAsync(world, runId);
        revision.Completeness.ShouldBe(ReconcileFinisher.Complete);
        revision.Missing.ShouldBeEmpty("every planned execution has its required trace resolved to a case attempt");
        revision.ArtifactSetHash.ShouldNotBeNullOrEmpty();
        (await StateAsync(world, runId)).ShouldBe("complete");
        (await PointerAsync(world, runId)).ShouldBe(revision.Id);
        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ late evidence helpers

    /// <summary>The queue an outbox row lands on when the dispatcher has no route for its type.</summary>
    private const string DefaultQueue = "default";

    /// <summary>
    /// A queued world whose outbox writer refuses the reconcile trigger after
    /// <paramref name="allowedReconcileEnqueues"/> rows: the seam that fails an ingest attempt after it has written its
    /// evidence, so a case can prove the evidence and the trigger are one transaction.
    /// </summary>
    /// <param name="allowedReconcileEnqueues">How many reconcile rows the writer accepts before it throws.</param>
    private async Task<IngestTestWorld> StartWithRefusingOutboxAsync(int allowedReconcileEnqueues)
    {
        var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            slots: 1,
            services: services =>
            {
                services.RemoveAll<IOutboxWriter>();
                services.AddSingleton<IOutboxWriter>(provider => new RefusingOutboxWriter(
                    new OutboxWriter(
                        provider.GetRequiredService<ITenantDb>(),
                        provider.GetRequiredService<ITenantScopeAccessor>()),
                    RunReconcileContract.OutboxTaskType,
                    allowedReconcileEnqueues));
            });
        return await AnswerPlanOrDisposeAsync(world, skip: 1);
    }

    /// <summary>
    /// The real outbox writer behind a refusal: it accepts the first <paramref name="allowed"/> rows of
    /// <paramref name="refusedTaskType"/> and throws on the next, so the failure lands after real writes of the same
    /// transaction instead of before them.
    /// </summary>
    /// <param name="inner">The writer every accepted row goes through.</param>
    /// <param name="refusedTaskType">The task type this writer refuses, counted per accepted row.</param>
    /// <param name="allowed">How many rows of that type it accepts first.</param>
    private sealed class RefusingOutboxWriter(IOutboxWriter inner, string refusedTaskType, int allowed) : IOutboxWriter
    {
        private int _written;

        public Task<Guid> EnqueueAsync(
            string taskType,
            string payloadJson,
            string aggregateType,
            Guid aggregateId,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(taskType, refusedTaskType, StringComparison.Ordinal)
                && Interlocked.Increment(ref _written) > allowed)
            {
                throw new InvalidOperationException(
                    $"refused the {taskType} enqueue after {allowed} row(s), to roll the attempt back");
            }

            return inner.EnqueueAsync(
                taskType,
                payloadJson,
                aggregateType,
                aggregateId,
                idempotencyKey,
                cancellationToken);
        }
    }

    /// <summary>
    /// Opens the run's late window. The window ends at <c>collection_deadline_at + late_window_minutes</c> (the ingest
    /// route's own SQL, both columns of the locked run row), so a deadline five minutes back with a five-minute window
    /// puts the end one minute ahead of the database clock: inside the window, as a producer's late evidence arrives.
    /// </summary>
    private Task OpenLateWindowAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            "update public.run set late_window_minutes = 5, collection_deadline_at = now() - interval '4 minutes' "
            + $"where id = '{runId:D}'");

    /// <summary>
    /// Closes the run's late window: the same columns, one minute past the end (<c>collection_deadline_at =
    /// now() - 5 minutes - 1 second</c>), which is the state a run is in when its window is spent.
    /// </summary>
    private Task CloseLateWindowAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            "update public.run set late_window_minutes = 5, collection_deadline_at = "
            + $"now() - interval '5 minutes' - interval '1 second' where id = '{runId:D}'");

    /// <summary>
    /// The run's late window as the ingest route evaluates it
    /// (<c>now() &lt;= collection_deadline_at + late_window_minutes</c>), so a case can prove a scenario really is inside
    /// or outside the window instead of trusting the arithmetic in its own comment. The reconciler does not read it: the
    /// route's decision at ingest time is the one that counts.
    /// </summary>
    private static Task<bool> LateWindowOpenAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<bool>(
            "select now() <= collection_deadline_at + make_interval(mins => late_window_minutes) "
            + $"from public.run where id = '{runId:D}'");

    /// <summary>
    /// The reconcile triggers the ingest path enqueued for one run's late evidence: the key shape is the contract's,
    /// so a count here is exactly the number of attempts a late batch asked for.
    /// </summary>
    private static Task<long> LateTriggerRowsAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync(
            "outbox",
            where: $"task_type = '{RunReconcileContract.OutboxTaskType}' "
            + $"and idempotency_key like 'orchestrator:run:{runId:D}:reconcile:late:%'");

    /// <summary>
    /// The same rows' idempotency keys, ordered, as one delimited scalar — the per-producer shape a multi-shard batch
    /// must produce, which a count alone cannot show.
    /// </summary>
    private static async Task<string[]> LateTriggerKeysAsync(IngestTestWorld world, Guid runId) =>
        (await world.ScalarAsync<string>(
            "select string_agg(idempotency_key, ',' order by idempotency_key) from public.outbox "
            + $"where task_type = '{RunReconcileContract.OutboxTaskType}' "
            + $"and idempotency_key like 'orchestrator:run:{runId:D}:reconcile:late:%'")).Split(',');

    /// <summary>
    /// One <c>evidence_revision</c> row of the run as a delimited scalar — id, number, completeness, codes, watermark,
    /// manifest hash, artifact-set hash and <c>created_at</c> — so an "untouched" assertion compares every column a
    /// rewrite would move rather than the two the summary reads.
    /// </summary>
    private static Task<string> RevisionRowAsync(IngestTestWorld world, Guid runId, int revisionNo) =>
        world.ScalarAsync<string>(
            "select er.id::text || '|' || er.revision_no::text || '|' || er.completeness || '|' || "
            + "array_to_string(er.missing, ',') || '|' || er.event_watermark::text || '|' || er.manifest_hash || '|' || "
            + "coalesce(er.artifact_set_hash, '') || '|' || er.created_at::text "
            + $"from public.evidence_revision er where er.org_id = '{world.Org}' and er.run_id = '{runId:D}' "
            + $"and er.revision_no = {revisionNo}");

    /// <summary>One lower-case boolean column of one stored event of the world's run, read by its producer sequence.</summary>
    private static Task<bool> EventFlagAsync(IngestTestWorld world, long sequence, string column) =>
        world.ScalarAsync<bool>(
            $"select {column} from public.event where run_id = '{world.RunId:D}' and sequence = {sequence}");

    /// <summary>Moves the committed outbox rows into the queue, then claims them until the expected attempts ran.</summary>
    /// <param name="world">The world whose host carries the queue.</param>
    /// <param name="expectedReconciles">How many reconcile rows must reach <c>done</c>.</param>
    private static async Task DispatchAndReconcileAsync(IngestTestWorld world, int expectedReconciles)
    {
        var dispatched = await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);
        dispatched.Enqueued.ShouldBeGreaterThanOrEqualTo(
            expectedReconciles,
            customMessage: "the trigger the ingest path wrote reaches the queue");

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 3 * expectedReconciles + 3; attempt++)
        {
            if (await DoneReconcilesAsync(world) >= expectedReconciles)
            {
                return;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        (await DoneReconcilesAsync(world)).ShouldBeGreaterThanOrEqualTo(
            expectedReconciles,
            customMessage: "every queued reconcile is claimed and completed by the consumer");
    }

    /// <summary>How many reconcile task rows of the world's org ever reached <c>done</c>.</summary>
    private static Task<long> DoneReconcilesAsync(IngestTestWorld world) =>
        world.CountAsync(
            "task",
            $"org_id = '{world.Org:D}' and task_type = '{RunReconcileContract.OutboxTaskType}' and state = 'done'");

    /// <summary>
    /// A run that finished as <c>incomplete</c> with one planned execution unanswered and one declared event missing:
    /// the shard declares two events, stores the first, and the plan's second execution records no attempt. The first
    /// revision then names <c>count_mismatch</c> and <c>missing_result</c>, and the evidence that closes both — the
    /// second event and its attempt — is what arrives late.
    /// </summary>
    /// <param name="world">The queued world, whose plan keeps one execution unattempted.</param>
    /// <param name="closeSecondShard">
    /// Closes the second planned shard too, declaring no more than an empty shard. A two-shard world whose second slot is
    /// still open compares as <c>pending</c>, not <c>incomplete</c>, so a two-shard case closes both slots.
    /// </param>
    /// <param name="declaredLastSequence">The first shard's declared high-water mark, its <c>closed_at</c> set with it.</param>
    /// <param name="declaredCount">The first shard's declared event count.</param>
    private async Task<(Guid RunId, string RevisionOne)> StartIncompleteRunAsync(
        IngestTestWorld world,
        bool closeSecondShard = false,
        long declaredLastSequence = 2,
        int declaredCount = 2)
    {
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence, declaredCount);
        if (closeSecondShard)
        {
            await CloseSlotAsync(world, runId, SecondProducer, declaredLastSequence: 0, declaredCount: 0);
        }
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.step", null, ExecutionKey: world.Plan.Executions[0].ExecutionKey));

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);
        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete, Describe(outcome));
        (await StateAsync(world, runId)).ShouldBe("incomplete");

        return (runId, await RevisionRowAsync(world, runId, 1));
    }
}
