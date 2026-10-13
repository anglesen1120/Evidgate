// 04.1.1-T4: the producer/sequence rules of POST /v1/runs/{r}/events — out-of-order arrivals that must be
// stored as sent, producer restart semantics (T84/T54) and the first-event `queued -> running` transition
// (T13) with the lock order that keeps two racing first batches from deadlocking (40P01, 05.3.1's rule).
// Real host, real C2 pipeline, real PostgreSQL; the request helpers are shared with the IngestQa* suites.
//
// Where an equivalent case already exists, this file names it instead of duplicating it:
//   * `new_producer_id_rejected_422_unknown_producer` -> IngestEventBatchTests.unknown_producer_422
//   * `events_after_cancel_marked`                    -> IngestEventBatchTests.canceled_run_accepts_and_marks_after_cancel
//                                                        + IngestQaBoundaryTests.after_cancel_accepted_run_terminal
//   * `late_events_marked_and_run_closed_after_window` -> IngestQaBoundaryTests.incomplete_run_late_events_marked_and_run_closed_after_window
//   * duplicate `(run, producer, sequence)`            -> IngestEventBatchTests.reused_sequence_rejects_422 (AC6)
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.1.1-T4: ordering, restart and the first-event run transition of <c>POST /v1/runs/{r}/events</c>.
/// <para>
/// The seeded run's INSERT leaves <c>run.state</c> at 0031's <c>created</c> default, while 05.2.1-T4 creates
/// a real run as <c>queued</c> (00 §8.1; the <c>created → queued</c> edge has no caller). The cases that
/// exercise the transition therefore set <c>queued</c> first, which is the state a real run is in when its
/// first batch arrives.
/// </para>
/// <para>
/// Every event of these cases carries its own planned <c>execution_key</c> with <c>attempt_no</c> 1: the
/// case-attempt rule (AC5) refuses a second event for the same <c>(execution_key, attempt_no)</c> whose
/// canonical payload differs — 409 <c>duplicate_different_payload</c> — so a follow-up batch of the same
/// producer names the next planned execution instead of reusing the first batch's key.
/// </para>
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class IngestOrderingTests
{
    private readonly PostgresTenancyFixture _fixture;

    public IngestOrderingTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static Task<long> EventRowsAsync(IngestTestWorld world) => world.CountAsync("event");

    /// <summary>The applied <c>queued → running</c> edges of the run: one outbox row per applied transition (T82).</summary>
    private static Task<long> StateChangedRowsAsync(IngestTestWorld world) =>
        world.CountAsync("outbox", "task_type = 'orchestrator.run.state_changed'");

    /// <summary>The transition's canonical audit rows (05.3.1 AC5).</summary>
    private static Task<long> TransitionAuditRowsAsync(IngestTestWorld world) =>
        world.CountAsync("audit_log", "action = 'run.state_changed'");

    private static Task<string> RunStateAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{world.RunId:D}'");

    /// <summary>The producer's recorded high-water mark, the reconciliation anchor (04.1.1-T4.1 §3).</summary>
    private static Task<long> LastSequenceAsync(IngestTestWorld world) =>
        world.ScalarAsync<long>("select last_sequence from public.producer_slot where producer_id = 'shard-1'");

    // ---------------------------------------------------------------- T13: first accepted batch

    [Fact]
    public async Task first_event_moves_run_to_running()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("queued");

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await RunStateAsync(world)).ShouldBe("running");

        // T13 + 05.3.1 AC5: exactly one applied edge — one `run.state_changed` outbox row and one audit row.
        (await StateChangedRowsAsync(world)).ShouldBe(1);
        (await TransitionAuditRowsAsync(world)).ShouldBe(1);

        // The next batch on the now-running run is accepted and never re-transitions (T82 no-op, T101: no
        // stronger lock than the shared read is taken once the run has left `queued`).
        var second = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 2)));

        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        (await RunStateAsync(world)).ShouldBe("running");
        (await StateChangedRowsAsync(world)).ShouldBe(1);
        (await TransitionAuditRowsAsync(world)).ShouldBe(1);
        (await EventRowsAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task refused_batch_leaves_run_queued()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("queued");

        // A well-formed key that no planned execution carries: the refusal lands after the transition was
        // applied, so this proves the applied transition rolls back with the refused batch (no partial state).
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("unknown_execution_key");
        (await EventRowsAsync(world)).ShouldBe(0);
        (await RunStateAsync(world)).ShouldBe("queued");
        (await StateChangedRowsAsync(world)).ShouldBe(0);
        (await TransitionAuditRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task canceled_run_batch_never_transitions()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("canceled");

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        // T12: a canceled run still accepts and marks the event, and the transition is never called on it.
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await world.ScalarAsync<bool>("select after_cancel from public.event where sequence = 1")).ShouldBeTrue();
        (await RunStateAsync(world)).ShouldBe("canceled");
        (await StateChangedRowsAsync(world)).ShouldBe(0);
        (await TransitionAuditRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task concurrent_first_batches_transition_once_without_deadlock()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("queued");

        // Two first batches of the same run in flight together, each with its own event and sequence but the
        // same (single) producer slot. This is the pair that deadlocked 40P01 when each transaction held the
        // run row FOR SHARE (the scope read) and then upgraded to FOR NO KEY UPDATE (the transition): the
        // handler now runs the transition BEFORE any shared read, so the strong lock is taken first.
        var first = IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));
        var second = IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 2)));

        var answers = await Task.WhenAll(first, second);

        // Both succeed — a deadlock would surface as a 500 (`internal_error`) on one of them.
        answers[0].Status.ShouldBe(HttpStatusCode.OK, answers[0].ErrorMessage);
        answers[1].Status.ShouldBe(HttpStatusCode.OK, answers[1].ErrorMessage);

        // One winner and one T82 no-op: the edge is applied exactly once, and both batches commit their event.
        (await RunStateAsync(world)).ShouldBe("running");
        (await StateChangedRowsAsync(world)).ShouldBe(1);
        (await TransitionAuditRowsAsync(world)).ShouldBe(1);
        (await EventRowsAsync(world)).ShouldBe(2);
    }

    // ---------------------------------------------------------------- T4.1: ordering

    [Fact]
    public async Task out_of_order_events_stored_not_reordered()
    {
        // `planned: 3`: every event of this test carries its own planned execution_key (see the class doc), and
        // the run's plan must cover all three.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 3);

        // Two batches that arrive in an order no clock agrees with: the first carries sequence 5 before 1 and
        // `occurred_at` that runs backwards, the second carries sequence 3 — a hole (2 and 4 are never sent)
        // and a sequence below an already-stored high-water mark. None of it may reorder or reject anything.
        var first = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 5, occurredAt: "2026-09-26T10:00:00Z"),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 1, occurredAt: "2026-09-26T12:00:00Z")));

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Body.GetProperty("accepted").GetInt32().ShouldBe(2);

        var second = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 2), sequence: 3, occurredAt: "2026-09-26T09:00:00Z")));

        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.Body.GetProperty("accepted").GetInt32().ShouldBe(1);

        // Stored as sent, in arrival order: `event.id` is the server UUIDv7 minted in request order, so
        // `order by id` is 5,1,3 — while `occurred_at` order would be 3,5,1 and sequence order 1,3,5. Nothing
        // was renumbered or reordered on the way in, and no batch was refused for the hole or the inversion.
        (await world.ScalarAsync<string>("select string_agg(sequence::text, ',' order by id) from public.event"))
            .ShouldBe("5,1,3");
        (await world.ScalarAsync<string>(
            "select string_agg(to_char(occurred_at at time zone 'UTC', 'HH24:MI'), ',' order by id) from public.event"))
            .ShouldBe("10:00,12:00,09:00");

        // The reader's order is `(producer_id, sequence)`, served by `event_run_producer_sequence_key` (0034).
        (await world.EventColumnAsync<long>("sequence")).ShouldBe([1L, 3L, 5L]);

        // The slot records the high-water mark, the reconciliation anchor a gap is derived from (no gap column):
        // last_sequence = 5 while only three events exist, so 2 and 4 were never stored.
        (await LastSequenceAsync(world)).ShouldBe(5L);
        (await EventRowsAsync(world)).ShouldBe(3);
    }

    // ---------------------------------------------------------------- T4.2 / T84: restart

    [Fact]
    public async Task restart_same_producer_resumes()
    {
        // `planned: 4`: four events, each on its own planned execution_key (see the class doc), so the restart
        // is about the producer/sequence pair alone and never about a reused attempt slot.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 4);

        // Before the restart: the producer committed sequences 1 and 2, so its checkpoint is 2.
        var before = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 2)));

        before.Status.ShouldBe(HttpStatusCode.OK, before.ErrorMessage);
        (await LastSequenceAsync(world)).ShouldBe(2L);

        // The restart reuses the same deterministic producer_id (T54) and resumes at checkpoint + 1: sequences
        // 3 and 4 are accepted, and the checkpoint advances. The slot stays bound to the same identity (no
        // rebind, no refusal); a producer_id the manifest never declared is the 422 case in
        // IngestEventBatchTests.unknown_producer_422.
        var after = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 2), sequence: 3),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 3), sequence: 4)));

        after.Status.ShouldBe(HttpStatusCode.OK, after.ErrorMessage);
        after.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        after.Body.GetProperty("duplicates").GetInt32().ShouldBe(0);
        (await LastSequenceAsync(world)).ShouldBe(4L);
        (await world.ScalarAsync<string>("select bound_token_jti from public.producer_slot where producer_id = 'shard-1'"))
            .ShouldBe(IngestTestWorld.Jti);
        (await EventRowsAsync(world)).ShouldBe(4);
    }

    // ---------------------------------------------------------------- a refusal after the transition

    [Fact]
    public async Task deleting_project_batch_refused_run_stays_queued()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("queued");
        await DeleteProjectAsync(world);

        // The project-state check runs after the transition (like the planned-key gate), so this also proves the
        // rollback half of the wiring: the applied `queued -> running` edge must be back to `queued` with no
        // event, no outbox row and no audit row once the whole batch is refused.
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("project_deleting");
        (await EventRowsAsync(world)).ShouldBe(0);
        (await RunStateAsync(world)).ShouldBe("queued");
        (await StateChangedRowsAsync(world)).ShouldBe(0);
        (await TransitionAuditRowsAsync(world)).ShouldBe(0);
    }

    /// <summary>The <c>deleted</c> project status the 06.1.2 suite uses for its 409 <c>project_deleting</c> case.</summary>
    private static Task DeleteProjectAsync(IngestTestWorld world) =>
        world.Fixture.ExecuteAsync(
            world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");
}
