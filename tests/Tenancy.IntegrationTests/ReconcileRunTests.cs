// 05.3.2-T2.1/T2.2 against a real PostgreSQL: the reconciler runs inside the caller's tenant transaction, locks the run
// row FOR NO KEY UPDATE first (never FOR SHARE: a later revision insert re-takes that lock, so a share->update upgrade
// deadlocks), reads the planned shards from producer_slot and the observed events in ONE grouped aggregate over event,
// and reports the declared-versus-observed comparison. Nothing here changes the run: T2.1/T2.2 only report, so every
// case asserts the run row and the (absent) evidence_revision row stay untouched.
//
// The comparison itself (every 00 §8.5 code from event evidence, the ordinal reason order, the open-shard short circuit)
// is ReconcileComparerUnitTests.cs; this file is the database half. Test-name anchors come from 05.3.2 §9/§10
// (T2.1: ReconcileRunTask_ClaimedOnceUnderSkipLocked; T2.2: Reconcile_EachMissingReason_IncompleteWithCode) and the
// coordinator's S119 contract update (a test.started attempt with no test.finished is unknown_status).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>One stored, acknowledged event of the run (the columns the aggregate reads).</summary>
    /// <param name="ProducerId">The producer (shard) that sent it.</param>
    /// <param name="Sequence">The producer's sequence; unique per <c>(org, run, producer)</c>.</param>
    /// <param name="Type">The event type, e.g. <c>test.finished</c>.</param>
    /// <param name="NormalizedStatus">The normalized status, or null for "the producer reported none".</param>
    /// <param name="ExecutionKey">The attempt's <c>execution_key</c> (<c>^[0-9a-f]{64}$</c>); derived when null.</param>
    /// <param name="AttemptNo">The attempt number (1-based).</param>
    private sealed record StoredEvent(
        string ProducerId,
        long Sequence,
        string Type,
        string? NormalizedStatus,
        string? ExecutionKey = null,
        int AttemptNo = 1);

    private readonly PostgresTenancyFixture _fixture;

    public ReconcileRunTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ world and seeding

    /// <summary>One migrated database with an org, its project, a run and <paramref name="slots"/> producer slots.</summary>
    private Task<IngestTestWorld> StartAsync(int slots = 1) =>
        IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), slots: slots);

    /// <summary>
    /// The consumer tests need the host to carry the queue mechanism too: the production
    /// <c>AddEvidgateTasks</c> over the host's own app-role data source, with no hosted service, so nothing claims a row
    /// before the test drives the poll (the composition <c>IngestTraceCorrelationTests</c> uses).
    /// </summary>
    private Task<IngestTestWorld> StartWithQueueAsync() =>
        IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            services: services => services.AddEvidgateTasks(new ConfigurationBuilder().Build()));

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
                   normalized_status, payload_hash)
                values (@id, @org_id, @run_id, @event_id, @producer_id, @sequence, @execution_key, @attempt_no, @type,
                        @normalized_status, @payload_hash)
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
                ("payload_hash", "sha256:" + new string('b', 64)))).ShouldBe(1);
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

    // ------------------------------------------------------------------ reads (the migration role: one database per test)

    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    private static Task<int> VersionAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<int>($"select version from public.run where id = '{runId:D}'");

    private static Task<DateTime?> UpdatedAtAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<DateTime?>($"select updated_at from public.run where id = '{runId:D}'");

    private static Task<long> RevisionCountAsync(IngestTestWorld world) => world.CountAsync("evidence_revision");

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

    // ------------------------------------------------------------------ T2.2: every reason the events support

    /// <summary>
    /// One case per event-evidence reason of 00 §8.5 with the code as the anchor: a shard whose slot never closed
    /// (<c>missing_shard</c>), a closed shard whose stored events disagree with its declared pair in each of the four
    /// ways the aggregate can see it — too few, a lower declared count than the events up to the high-water mark, events
    /// past the declared high-water mark, a gap, and storage out of sequence order (the comparison is order-insensitive,
    /// so a gap is a count disagreement, never a sequence walk) — and a closed shard with a status the producer never
    /// normalized (<c>unknown_status</c>). Each case is incomplete evidence with exactly that reason for that producer.
    /// </summary>
    /// <param name="code">The 00 §8.5 code the evidence must name.</param>
    /// <param name="scenario">Which shape of evidence <see cref="ArrangeEventEvidenceAsync"/> seeds.</param>
    [Theory]
    [InlineData(ReconcileMissingCodes.MissingShard, "open_shard")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "short")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "over")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "beyond_declared")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "gap")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "out_of_order")]
    [InlineData(ReconcileMissingCodes.UnknownStatus, "null_status")]
    public async Task Reconcile_EachMissingReason_IncompleteWithCode(string code, string scenario)
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await ArrangeEventEvidenceAsync(world, runId, scenario);

        var outcome = await ReconcileAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.EventsMatch.ShouldBeFalse("incomplete evidence is never a match");
        comparison.Missing.Count.ShouldBe(1, Describe(outcome));
        comparison.Missing[0].Code.ShouldBe(code, Describe(outcome));
        comparison.Missing[0].ProducerId.ShouldBe(Producer);
        (await StateAsync(world, runId)).ShouldBe("collecting");
        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// The S119 rule: every code the event evidence can name leaves the comparison incomplete, so no reason can be
    /// mistaken for a completeness verdict (T2.3/T2.4 decide completeness, never an empty <c>missing[]</c> alone).
    /// </summary>
    /// <param name="code">The code the case must produce.</param>
    /// <param name="scenario">Which shape of evidence <see cref="ArrangeEventEvidenceAsync"/> seeds.</param>
    [Theory]
    [InlineData(ReconcileMissingCodes.MissingShard, "open_shard")]
    [InlineData(ReconcileMissingCodes.CountMismatch, "short")]
    [InlineData(ReconcileMissingCodes.UnknownStatus, "null_status")]
    public async Task Reconcile_EveryCodeMeansIncomplete(string code, string scenario)
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await ArrangeEventEvidenceAsync(world, runId, scenario);

        var outcome = await ReconcileAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.EventsMatch.ShouldBeFalse(Describe(outcome));
        comparison.Missing.ShouldNotBeEmpty();
        comparison.Missing.ShouldAllBe(reason => !string.IsNullOrWhiteSpace(reason.Code));
        comparison.Missing.Select(reason => reason.Code).ShouldContain(code, Describe(outcome));
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
    /// the type. Its reconcile leaves no row behind (T2.2 reports only), which is why the assertion is the task row's own
    /// state and attempt count.
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
        (await RevisionCountAsync(world)).ShouldBe(0, "T2.2 persists no revision, however often the row is claimed");
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
