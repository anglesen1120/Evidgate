// 04.1.1-T2: the event_id dedupe path and the sequence_reused mapping of POST /v1/runs/{r}/events through the real
// host and a real PostgreSQL — AC2 (resend is all duplicates and meters nothing, T83), AC5 (same event_id, different
// canonical payload → 409 with the failing index, T50) and AC6/D3 (a reused (run, producer_id, sequence) → 422 and
// commits nothing, including the run-state transition the batch had already applied). The named tests of the task
// file §8 (Telemetry/Failure rows) and §9; the request shapes mirror the sibling ingest suites, so the builders of
// IngestQaRequests and the world helpers of IngestTestWorld are reused rather than re-declared.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The dedupe half of the atomic batch ingest: one <c>insert … on conflict do nothing</c> plus at most one read-back
/// of the conflicted <c>event_id</c>s, the three outcomes that read-back tells apart (<c>accepted</c>,
/// <c>duplicate</c>, 409 <c>duplicate_different_payload</c>, 422 <c>sequence_reused</c>), and the metering rule that
/// only accepted events reach the <c>usage.batch_accepted</c> outbox row (T83, §7).
/// <para>
/// Every test asserts on committed rows, not on the handler's internals: a refusal is proven by the row and receipt
/// counts it leaves behind, the metering rule by the outbox payload itself (<c>event_batch_id</c>, <c>metric</c>,
/// <c>quantity</c>), and the "one insert, one read-back" budget by the host's Npgsql command log.
/// </para>
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class IngestDedupeTests
{
    /// <summary>The outbox task type of the per-batch usage row (04.8.1 §4, 04.1.1 §7).</summary>
    private const string UsageTaskType = "usage.batch_accepted";

    /// <summary>The outbox task type of the 05.3.1 run-state transition (<c>queued → running</c>).</summary>
    private const string TransitionTaskType = "orchestrator.run.state_changed";

    private readonly PostgresTenancyFixture _fixture;

    public IngestDedupeTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ reads the assertions need

    private static Task<long> EventRowsAsync(IngestTestWorld world) => world.CountAsync("event");

    private static Task<long> BatchRowsAsync(IngestTestWorld world) => world.CountAsync("event_batch");

    private static Task<long> UsageRowsAsync(IngestTestWorld world) =>
        world.CountAsync("outbox", $"task_type = '{UsageTaskType}'");

    private static Task<long> UsageRowsOfBatchAsync(IngestTestWorld world, Guid batchId) =>
        world.CountAsync("outbox", $"task_type = '{UsageTaskType}' and aggregate_id = '{batchId:D}'");

    private static Task<long> TransitionRowsAsync(IngestTestWorld world) =>
        world.CountAsync("outbox", $"task_type = '{TransitionTaskType}'");

    private static Task<long> TransitionAuditRowsAsync(IngestTestWorld world) =>
        world.CountAsync("audit_log", "action = 'run.state_changed'");

    private static Task<long> EventIdRowsAsync(IngestTestWorld world, params Guid[] eventIds) =>
        world.ScalarAsync<long>(
            "select count(*) from public.event where event_id in ("
            + string.Join(", ", eventIds.Select(id => $"'{id:D}'"))
            + ")");

    private static Task<int> ReceiptColumnAsync(IngestTestWorld world, Guid batchId, string column) =>
        world.ScalarAsync<int>($"select {column} from public.event_batch where id = '{batchId:D}'");

    private static Task<string> RunStateAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{world.RunId:D}'");

    private static Task<long> SlotLastSequenceAsync(IngestTestWorld world) =>
        world.ScalarAsync<long>(
            $"select last_sequence from public.producer_slot where run_id = '{world.RunId:D}' "
            + $"and producer_id = '{IngestTestWorld.ProducerId}'");

    /// <summary>The one usage row of <paramref name="batchId"/>, as the committed <c>payload</c> object.</summary>
    private static async Task<JsonElement> UsagePayloadAsync(IngestTestWorld world, Guid batchId)
    {
        var text = await world.ScalarAsync<string>(
            $"select payload::text from public.outbox where task_type = '{UsageTaskType}' "
            + $"and aggregate_id = '{batchId:D}'");
        using var document = JsonDocument.Parse(text);

        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------ response shapes

    private static int Accepted(ApiAnswer answer) => answer.Body.GetProperty("accepted").GetInt32();

    private static int Duplicates(ApiAnswer answer) => answer.Body.GetProperty("duplicates").GetInt32();

    private static Guid BatchId(ApiAnswer answer) => answer.Body.GetProperty("batch_id").GetGuid();

    private static List<string?> AckKinds(ApiAnswer answer) =>
        [.. IngestQaRequests.Acks(answer).Select(ack => ack.GetProperty("ack").GetString())];

    /// <summary>The one <c>error.details</c> row of a refusal (the handler names exactly the offending index).</summary>
    private static JsonElement SingleDetail(ApiAnswer answer)
    {
        var details = IngestQaRequests.Details(answer);
        details.Count.ShouldBe(1);

        return details[0];
    }

    /// <summary>The one statement that inserts the batch's event rows (<c>insert into public.event (…</c>).</summary>
    private static bool IsEventInsert(string statement) =>
        statement.Contains("insert into public.event (", StringComparison.Ordinal);

    /// <summary>
    /// The bounded conflict read-back (<c>… join jsonb_to_recordset(…) … e.payload_hash as "PayloadHash"</c>): one
    /// statement per batch at most, and none when the insert stored every event.
    /// </summary>
    private static bool IsConflictReadBack(string statement) =>
        statement.Contains("join jsonb_to_recordset(", StringComparison.Ordinal)
        && statement.Contains("payload_hash as", StringComparison.Ordinal);

    // ------------------------------------------------------------------ AC2/T83: the duplicate-only resend

    [Fact]
    public async Task duplicate_only_batch_emits_no_metering_row()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var body = IngestQaRequests.Batch(
            IngestQaRequests.Event(first, IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "failed"),
            IngestQaRequests.Event(second, IngestQaRequests.KeyOf(world, 1), sequence: 2, status: "passed"));

        var accepted = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        Accepted(accepted).ShouldBe(2);
        var eventsAfterFirst = await EventRowsAsync(world);
        var batchesAfterFirst = await BatchRowsAsync(world);
        var usageAfterFirst = await UsageRowsAsync(world);
        eventsAfterFirst.ShouldBe(2);
        usageAfterFirst.ShouldBe(1);

        var resend = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);

        // AC2: the identical resend is 200 with every event acked `duplicate`, never 409.
        resend.Status.ShouldBe(HttpStatusCode.OK, resend.ErrorMessage);
        Accepted(resend).ShouldBe(0);
        Duplicates(resend).ShouldBe(2);
        AckKinds(resend).ShouldBe(["duplicate", "duplicate"]);

        // AC2/T83: no new event row and no new metering row — accepted = 0 never bills.
        (await EventRowsAsync(world)).ShouldBe(eventsAfterFirst);
        (await UsageRowsAsync(world)).ShouldBe(usageAfterFirst);
        (await world.EventColumnAsync<Guid>("event_id")).ShouldBe([first, second]);

        // A new receipt row is allowed (it is the batch's own record of what it did) …
        var receipt = BatchId(resend);
        (await BatchRowsAsync(world)).ShouldBe(batchesAfterFirst + 1);
        (await ReceiptColumnAsync(world, receipt, "accepted")).ShouldBe(0);
        (await ReceiptColumnAsync(world, receipt, "duplicate")).ShouldBe(2);
        (await ReceiptColumnAsync(world, receipt, "event_count")).ShouldBe(2);

        // … but that receipt owns no usage row: the duplicate-only batch emitted none.
        (await UsageRowsOfBatchAsync(world, receipt)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ §7/Telemetry: quantity = accepted

    [Fact]
    public async Task accepted_batch_outbox_rows_carry_per_metric_quantity()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var firstAnswer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(first, IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "failed"),
                IngestQaRequests.Event(second, IngestQaRequests.KeyOf(world, 1), sequence: 2, status: "passed")));
        firstAnswer.Status.ShouldBe(HttpStatusCode.OK, firstAnswer.ErrorMessage);

        // One row per metric, quantity = the batch's accepted count, and event_batch_id names the receipt.
        var firstReceipt = BatchId(firstAnswer);
        var firstPayload = await UsagePayloadAsync(world, firstReceipt);
        firstPayload.GetProperty("event_batch_id").GetGuid().ShouldBe(firstReceipt);
        firstPayload.GetProperty("metric").GetString().ShouldBe("event");
        firstPayload.GetProperty("quantity").GetInt32().ShouldBe(2);
        (await UsageRowsOfBatchAsync(world, firstReceipt)).ShouldBe(1);

        // A batch mixing a resent event with a genuinely new one: only the new event is metered. The new event is a
        // test.step (no verdict, no attempt write), so the batch's only differences from the first are the dedupe
        // outcome and the accepted count.
        var third = Guid.CreateVersion7();
        var mixed = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(first, IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "failed"),
                IngestQaRequests.Event(third, IngestQaRequests.KeyOf(world, 1), sequence: 3, type: "test.step", status: null)));

        mixed.Status.ShouldBe(HttpStatusCode.OK, mixed.ErrorMessage);
        Accepted(mixed).ShouldBe(1);
        Duplicates(mixed).ShouldBe(1);
        AckKinds(mixed).ShouldBe(["duplicate", "accepted"]);

        var mixedReceipt = BatchId(mixed);
        var mixedPayload = await UsagePayloadAsync(world, mixedReceipt);
        mixedPayload.GetProperty("event_batch_id").GetGuid().ShouldBe(mixedReceipt);
        mixedPayload.GetProperty("metric").GetString().ShouldBe("event");

        // `quantity = accepted`: one, not two — the duplicate is never billed (T83).
        mixedPayload.GetProperty("quantity").GetInt32().ShouldBe(1);
        (await UsageRowsOfBatchAsync(world, mixedReceipt)).ShouldBe(1);
        (await ReceiptColumnAsync(world, mixedReceipt, "accepted")).ShouldBe(1);
        (await ReceiptColumnAsync(world, mixedReceipt, "duplicate")).ShouldBe(1);

        // Same transaction: the receipt and its metering row are both visible once the response returned.
        (await BatchRowsAsync(world)).ShouldBe(2);
        (await UsageRowsAsync(world)).ShouldBe(2);
        (await EventRowsAsync(world)).ShouldBe(3);
    }

    // ------------------------------------------------------------------ AC5: the payload hash decides

    [Fact]
    public async Task different_payload_conflict_cross_batch_and_in_batch_reports_index()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var stored = Guid.CreateVersion7();

        var accepted = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(stored, IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "passed")));
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        var eventsAfterFirst = await EventRowsAsync(world);
        var batchesAfterFirst = await BatchRowsAsync(world);
        var usageAfterFirst = await UsageRowsAsync(world);
        eventsAfterFirst.ShouldBe(1);

        // Cross-batch: the stored event_id reappears at index 2 of a later batch with a different payload.
        var newFirst = Guid.CreateVersion7();
        var newSecond = Guid.CreateVersion7();
        var crossBatch = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(newFirst, IngestQaRequests.KeyOf(world, 1), sequence: 2, type: "test.step", status: null),
                IngestQaRequests.Event(newSecond, IngestQaRequests.KeyOf(world, 1), sequence: 3, type: "test.step", status: null),
                IngestQaRequests.Event(stored, IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "failed")));

        crossBatch.Status.ShouldBe(HttpStatusCode.Conflict, crossBatch.ErrorMessage);
        crossBatch.ErrorCode.ShouldBe("duplicate_different_payload");

        // The detail names the offending position, not the first event of the batch.
        var crossDetail = SingleDetail(crossBatch);
        IngestQaRequests.IndexOf(crossDetail).ShouldBe(2);
        crossDetail.GetProperty("path").GetString().ShouldBe("/events/2/event_id");
        crossDetail.GetProperty("reason").GetString().ShouldBe("duplicate_different_payload");

        // Whole batch rejected: no new event row (the two genuinely new events too), receipt or metering row.
        (await EventRowsAsync(world)).ShouldBe(eventsAfterFirst);
        (await BatchRowsAsync(world)).ShouldBe(batchesAfterFirst);
        (await UsageRowsAsync(world)).ShouldBe(usageAfterFirst);
        (await EventIdRowsAsync(world, newFirst, newSecond)).ShouldBe(0);
        (await world.ScalarAsync<string>($"select raw_status from public.event where event_id = '{stored:D}'"))
            .ShouldBe("passed");

        // In-batch: the same event_id twice inside one request with different payloads (index 2 vs index 0).
        var repeated = Guid.CreateVersion7();
        var eventsBeforeInBatch = await EventRowsAsync(world);
        var batchesBeforeInBatch = await BatchRowsAsync(world);
        var usageBeforeInBatch = await UsageRowsAsync(world);

        var inBatch = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(repeated, IngestQaRequests.KeyOf(world, 0), sequence: 4, status: "passed"),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 5, status: "passed"),
                IngestQaRequests.Event(repeated, IngestQaRequests.KeyOf(world, 0), sequence: 4, status: "failed")));

        inBatch.Status.ShouldBe(HttpStatusCode.Conflict, inBatch.ErrorMessage);
        inBatch.ErrorCode.ShouldBe("duplicate_different_payload");

        var inBatchDetail = SingleDetail(inBatch);
        IngestQaRequests.IndexOf(inBatchDetail).ShouldBe(2);
        inBatchDetail.GetProperty("path").GetString().ShouldBe("/events/2/event_id");
        inBatchDetail.GetProperty("reason").GetString().ShouldBe("duplicate_different_payload");

        // The in-batch pass runs before any write, so nothing of this batch is stored either.
        (await EventRowsAsync(world)).ShouldBe(eventsBeforeInBatch);
        (await BatchRowsAsync(world)).ShouldBe(batchesBeforeInBatch);
        (await UsageRowsAsync(world)).ShouldBe(usageBeforeInBatch);
        (await EventIdRowsAsync(world, repeated)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC6/D3: the reused sequence

    [Fact]
    public async Task sequence_reused_rolls_back_everything()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // The seeded run is `created`: an accepted batch on it commits without any transition (only `queued` drives
        // one), so the refused batch below owns the run's first `queued → running` attempt and its rollback.
        var accepted = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "passed")));
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        (await RunStateAsync(world)).ShouldBe("created");
        (await SlotLastSequenceAsync(world)).ShouldBe(1);
        (await TransitionRowsAsync(world)).ShouldBe(0);

        // Put the run to queued, so the refused batch has a queued → running transition to roll back (D3).
        await world.SetRunStateAsync("queued");
        var eventsBefore = await EventRowsAsync(world);
        var batchesBefore = await BatchRowsAsync(world);
        var usageBefore = await UsageRowsAsync(world);
        var slotBefore = await SlotLastSequenceAsync(world);

        // Index 1 is a new event_id that reuses (run, shard-1, sequence 1): 422 with that index.
        var reused = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 2, status: "passed"),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "passed")));

        reused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, reused.ErrorMessage);
        reused.ErrorCode.ShouldBe("sequence_reused");

        var detail = SingleDetail(reused);
        IngestQaRequests.IndexOf(detail).ShouldBe(1);
        detail.GetProperty("path").GetString().ShouldBe("/events/1/sequence");
        detail.GetProperty("reason").GetString().ShouldBe("sequence_reused");

        // Nothing of the batch committed: no event (index 0's new event included), receipt, metering row, slot bump.
        (await EventRowsAsync(world)).ShouldBe(eventsBefore);
        (await BatchRowsAsync(world)).ShouldBe(batchesBefore);
        (await UsageRowsAsync(world)).ShouldBe(usageBefore);
        (await SlotLastSequenceAsync(world)).ShouldBe(slotBefore);
        (await world.EventColumnAsync<long>("sequence")).ShouldBe([1L]);

        // The queued → running transition this batch applied rolled back with it: still queued, no trace of it.
        (await RunStateAsync(world)).ShouldBe("queued");
        (await TransitionRowsAsync(world)).ShouldBe(0);
        (await TransitionAuditRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ §8 P2: one insert + one read-back

    [Fact]
    public async Task dedupe_write_statements_are_one_insert_plus_one_conflict_select()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        // A marker only the request body carries: it can reach a command text only by concatenation (S1).
        var sentinel = $"payload-literal-{first:N}";
        var body = IngestQaRequests.Batch(
            IngestQaRequests.Event(
                first, IngestQaRequests.KeyOf(world, 0), sequence: 1, type: "test.step", status: null,
                ext: new { schema = "playwright/1", marker = sentinel }),
            IngestQaRequests.Event(
                second, IngestQaRequests.KeyOf(world, 1), sequence: 2, type: "test.step", status: null));

        // An all-new batch: one event insert and no conflict read-back at all.
        world.Host.Statements.Clear();
        var allNew = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);

        allNew.Status.ShouldBe(HttpStatusCode.OK, allNew.ErrorMessage);
        Accepted(allNew).ShouldBe(2);

        var allNewStatements = world.Host.Statements.Snapshot();
        var allNewInserts = allNewStatements.Where(IsEventInsert).ToList();
        allNewInserts.Count.ShouldBe(1, string.Join(Environment.NewLine, allNewStatements));
        allNewStatements.Where(IsConflictReadBack).ShouldBeEmpty(string.Join(Environment.NewLine, allNewStatements));

        // The events array travels as one Npgsql parameter, never as a literal in the command text.
        allNewInserts[0].ShouldContain("jsonb_to_recordset(");
        allNewInserts[0].ShouldNotContain(first.ToString("D"));
        allNewStatements.ShouldAllBe(statement => !statement.Contains(sentinel, StringComparison.Ordinal));

        // A resend (every event a duplicate): still one insert, plus exactly one conflict read-back SELECT.
        world.Host.Statements.Clear();
        var resend = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);

        resend.Status.ShouldBe(HttpStatusCode.OK, resend.ErrorMessage);
        Duplicates(resend).ShouldBe(2);

        var resendStatements = world.Host.Statements.Snapshot();
        var resendInserts = resendStatements.Where(IsEventInsert).ToList();
        var resendReadBacks = resendStatements.Where(IsConflictReadBack).ToList();
        resendInserts.Count.ShouldBe(1, string.Join(Environment.NewLine, resendStatements));
        resendReadBacks.Count.ShouldBe(1, string.Join(Environment.NewLine, resendStatements));
        resendStatements.ShouldAllBe(statement => !statement.Contains(sentinel, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ tenant boundary of the read-back

    [Fact]
    public async Task conflict_select_never_matches_other_org_event_id()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var shared = Guid.CreateVersion7();

        var orgA = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(shared, IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "passed")));
        orgA.Status.ShouldBe(HttpStatusCode.OK, orgA.ErrorMessage);
        Accepted(orgA).ShouldBe(1);

        // A second tenant with its own project, run and producer slot; the run is already running, so the batch has
        // no state transition to perform.
        var (otherOrg, otherProject) = await world.CreateOtherOrgAsync("globex");
        var otherRun = await world.SeedOtherRunAsync(otherOrg);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set state = 'running' where id = '{otherRun.RunId:D}'");
        await IngestTestWorld.SeedSlotsAsync(_fixture, world.Database, otherOrg, otherRun.RunId, count: 1, boundJti: null);

        await using var otherHost = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), otherOrg, otherProject,
            IngestTestWorld.Writer(projectScope: otherProject));
        using var otherClient = otherHost.CreateClient();

        // The same event_id with a different payload on the other tenant's run: an ordinary accept, never a 409.
        var otherBody = IngestQaRequests.Batch(IngestQaRequests.Event(
            shared, otherRun.Executions[0].ExecutionKey, sequence: 1, status: "failed"));

        var other = await IngestQaRequests.PostEventsAsync(otherClient, otherRun.RunId, otherBody);
        other.Status.ShouldBe(HttpStatusCode.OK, other.ErrorMessage);
        other.ErrorCode.ShouldBeNull();
        Accepted(other).ShouldBe(1);
        Duplicates(other).ShouldBe(0);
        AckKinds(other).ShouldBe(["accepted"]);

        // Each tenant holds exactly its own row, with its own payload: the read-back never spans tenants.
        (await EventRowsAsync(world)).ShouldBe(2);
        (await world.ScalarAsync<long>(
            $"select count(*) from public.event where org_id = '{world.Org:D}' and event_id = '{shared:D}'")).ShouldBe(1);
        (await world.ScalarAsync<long>(
            $"select count(*) from public.event where org_id = '{otherOrg:D}' and event_id = '{shared:D}'")).ShouldBe(1);
        (await world.ScalarAsync<string>(
            $"select raw_status from public.event where org_id = '{world.Org:D}' and event_id = '{shared:D}'"))
            .ShouldBe("passed");
        (await world.ScalarAsync<string>(
            $"select raw_status from public.event where org_id = '{otherOrg:D}' and event_id = '{shared:D}'")).ShouldBe("failed");

        // The other tenant's resend is a duplicate of its own row, not a conflict with the first tenant's.
        var resend = await IngestQaRequests.PostEventsAsync(otherClient, otherRun.RunId, otherBody);
        resend.Status.ShouldBe(HttpStatusCode.OK, resend.ErrorMessage);
        Accepted(resend).ShouldBe(0);
        Duplicates(resend).ShouldBe(1);
        AckKinds(resend).ShouldBe(["duplicate"]);
        (await EventRowsAsync(world)).ShouldBe(2);
    }
}
