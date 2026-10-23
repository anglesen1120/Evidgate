// 05.3.2-T1: POST /v1/runs/{r}/finalize through the real host and a real PostgreSQL — the bound-identity check
// (T9), the shard check (DECISION Q2=A), the manifest_hash check (AC3), the idempotent producer-slot close (T68,
// D3), the running -> collecting transition (05.3.1's guarded transition) and the ONE reconcile outbox row per
// newly closed slot (05.3.2-T1.2).
//
// HTTP black box: nothing here references a type the handler declares. Every assertion reads producer_slot, run,
// outbox and audit_log through the fixture's superuser connection, and every request is the wire contract of
// contracts/openapi.yaml (operationId finalizeRun). The world, the token-minting seam and the run/slot seeding
// come from IngestTestWorld (04.1.1-T1) — the same service-principal seam the events suite uses, because a
// CredentialId (the slot-binding identity) cannot be expressed through request headers.
//
// 04.1.2-T5's plan row "finalize under foreign identity -> 403" (04.1.2 line 145) lives here under that plan's
// test name, Finalize_WrongIdentity_403ProducerSlotTaken; FinalizeBindingTests keeps driving CloseSlotAsync
// directly, and these are the endpoint-level cases.
//
// Not covered on purpose: "the reconcile outbox row is invisible before commit". The row is written inside the
// handler's ambient tenant transaction, and this harness has no seam to hold that transaction open between the
// insert and the commit (the request completes before the test can read). The refusal cases cover the other half —
// no row at all when nothing is applied.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class FinalizeEndpointTests
{
    /// <summary>The reconcile work item's outbox task type (05.3.2-T1.2; consumed by 05.3.2-T2).</summary>
    private const string ReconcileTaskType = "orchestrator.run.reconcile";

    /// <summary>The transition's outbox task type (<c>RunStateTransitionContract.OutboxTaskType</c>).</summary>
    private const string StateChangedTaskType = "orchestrator.run.state_changed";

    /// <summary>The seeded shard, bound to <see cref="IngestTestWorld.Jti"/> in most worlds.</summary>
    private const string Producer = IngestTestWorld.ProducerId;

    /// <summary>A well-formed <c>sha256:</c> hash that is not the seeded manifest's (<c>RunRowSeeder</c> hashes 'd').</summary>
    private static readonly string WrongHash = "sha256:" + new string('0', 64);

    private readonly PostgresTenancyFixture _fixture;

    public FinalizeEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ request

    /// <summary>
    /// One <c>POST /v1/runs/{r}/finalize</c> as the client's published principal. The <c>Idempotency-Key</c> is
    /// always sent (a fresh one unless a case wants a specific value) although the route does not arbitrate on it:
    /// the arbiter of a duplicate is <c>producer_slot.closed_at</c> (D3), so a case that repeats with a DIFFERENT
    /// key still proves the slot decides.
    /// </summary>
    private static async Task<ApiAnswer> FinalizeAsync(
        HttpClient client,
        Guid runId,
        object body,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/runs/{runId:D}/finalize");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey ?? NewKey());
        request.Content = JsonContent.Create(body);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>
    /// The finalize body of the contract (<c>producer_id</c>, <c>shard</c>, <c>last_sequence</c>, <c>count</c>,
    /// <c>manifest_hash</c>), with the world's real manifest hash unless a case overrides it.
    /// </summary>
    private static async Task<Dictionary<string, object?>> BodyAsync(
        IngestTestWorld world,
        string producerId = Producer,
        int shard = 1,
        long lastSequence = 3,
        int count = 3,
        string? manifestHash = null) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["producer_id"] = producerId,
            ["shard"] = shard,
            ["last_sequence"] = lastSequence,
            ["count"] = count,
            ["manifest_hash"] = manifestHash ?? await ManifestHashAsync(world),
        };

    /// <summary>
    /// A service principal with <c>event:write</c>, a project scope and a claimed CI run reference (T88) — the
    /// <see cref="IngestTestWorld.Writer"/> seam plus the CI claims the headers cannot carry.
    /// </summary>
    private static RequestPrincipal CiWriter(string jti, Guid? projectScope, CiRunReference? ciRun) =>
        RequestPrincipal.Service(projectScope, [Permissions.Event.Write], credentialId: jti, ciRun: ciRun);

    /// <summary>A host whose requests carry <paramref name="principal"/> for the world's org and project.</summary>
    private TenancyApiFactory HostFor(IngestTestWorld world, RequestPrincipal principal) =>
        TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project, principal);

    // ------------------------------------------------------------------ reads (superuser: one database per test)

    /// <summary>The run's manifest hash, through <c>run.manifest_id</c> (same tenant).</summary>
    private static Task<string> ManifestHashAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>(
            "select m.manifest_hash from public.manifest m "
            + $"join public.run r on r.org_id = m.org_id and r.manifest_id = m.id where r.id = '{world.RunId:D}'");

    private static Task<string> RunStateAsync(IngestTestWorld world, Guid? runId = null) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId ?? world.RunId}'");

    private static Task<int> RunVersionAsync(IngestTestWorld world, Guid? runId = null) =>
        world.ScalarAsync<int>($"select version from public.run where id = '{runId ?? world.RunId}'");

    private static Task<bool> SlotClosedAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<bool>(
            $"select closed_at is not null from public.producer_slot where producer_id = '{producerId}'");

    /// <summary>The stored declared high-water mark; <c>-1</c> while the slot is open (the column is NULL then).</summary>
    private static Task<long> SlotDeclaredLastSequenceAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<long>(
            $"select coalesce(declared_last_sequence, -1) from public.producer_slot where producer_id = '{producerId}'");

    /// <summary>The stored declared count; <c>-1</c> while the slot is open (the column is NULL then).</summary>
    private static Task<int> SlotDeclaredCountAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<int>(
            $"select coalesce(declared_count, -1) from public.producer_slot where producer_id = '{producerId}'");

    /// <summary>True while the slot declares nothing — the open state migration 0035's all-or-nothing CHECK allows.</summary>
    private static Task<bool> SlotDeclaresNothingAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<bool>(
            "select declared_last_sequence is null and declared_count is null "
            + $"from public.producer_slot where producer_id = '{producerId}'");

    private static Task<int> SlotVersionAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<int>(
            $"select version from public.producer_slot where producer_id = '{producerId}'");

    /// <summary>The bound token id; empty while the slot is unbound.</summary>
    private static Task<string> SlotBoundJtiAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<string>(
            $"select coalesce(bound_token_jti, '') from public.producer_slot where producer_id = '{producerId}'");

    /// <summary>The reconcile triggers written for the world's run (never for a refused or repeated finalize).</summary>
    private static Task<long> ReconcileRowsAsync(IngestTestWorld world) =>
        world.CountAsync("outbox", $"task_type = '{ReconcileTaskType}' and aggregate_id = '{world.RunId:D}'");

    /// <summary>The applied transitions into <paramref name="to"/> for the world's run.</summary>
    private static Task<long> StateChangedRowsAsync(IngestTestWorld world, string to) =>
        world.CountAsync(
            "outbox",
            $"task_type = '{StateChangedTaskType}' and aggregate_id = '{world.RunId:D}' and payload->>'to' = '{to}'");

    /// <summary>Every outbox row of the test's database: a refusal writes none at all.</summary>
    private static Task<long> OutboxRowsAsync(IngestTestWorld world) => world.CountAsync("outbox");

    /// <summary>The transition's canonical audit entry (05.3.1 AC5).</summary>
    private static Task<long> TransitionAuditRowsAsync(IngestTestWorld world) =>
        world.CountAsync("audit_log", $"action = 'run.state_changed' and object_id = '{world.RunId:D}'");

    private static async Task<JsonElement> ReconcilePayloadAsync(IngestTestWorld world)
    {
        var text = await world.ScalarAsync<string>(
            $"select payload::text from public.outbox where task_type = '{ReconcileTaskType}' and aggregate_id = '{world.RunId:D}'");
        using var document = JsonDocument.Parse(text);

        return document.RootElement.Clone();
    }

    private static Task<string> ReconcileKeyAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>(
            $"select idempotency_key from public.outbox where task_type = '{ReconcileTaskType}' and aggregate_id = '{world.RunId:D}'");

    private static Task<string> ReconcileAggregateTypeAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>(
            $"select aggregate_type from public.outbox where task_type = '{ReconcileTaskType}' and aggregate_id = '{world.RunId:D}'");

    /// <summary>The 00 §9.2 <c>error.details</c> rows of a refusal body.</summary>
    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    // ------------------------------------------------------------------ AC1: the accepted finalize

    [Fact]
    public async Task Finalize_BoundIdentity_ManifestHashMatches_202_SlotClosed_RunCollecting()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");
        var runVersion = await RunVersionAsync(world);
        var slotVersion = await SlotVersionAsync(world);

        var answer = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world));

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);

        // The slot is closed exactly once and stores the declared values (T68) — one write, so one version bump.
        (await SlotClosedAsync(world)).ShouldBeTrue();
        (await SlotDeclaredLastSequenceAsync(world)).ShouldBe(3L);
        (await SlotDeclaredCountAsync(world)).ShouldBe(3);
        (await SlotVersionAsync(world)).ShouldBe(slotVersion + 1);

        // running -> collecting through the guarded transition: one edge, one version bump, one audit entry.
        (await RunStateAsync(world)).ShouldBe("collecting");
        (await RunVersionAsync(world)).ShouldBe(runVersion + 1);
        (await StateChangedRowsAsync(world, "collecting")).ShouldBe(1);
        (await StateChangedRowsAsync(world, "running")).ShouldBe(0);
        (await TransitionAuditRowsAsync(world)).ShouldBe(1);

        // ONE reconcile trigger for the newly closed slot, carrying what the shard declared (05.3.2-T2 reads it).
        (await ReconcileRowsAsync(world)).ShouldBe(1);
        (await ReconcileAggregateTypeAsync(world)).ShouldBe("run");
        (await ReconcileKeyAsync(world)).ShouldBe($"orchestrator:run:{world.RunId:D}:reconcile:{Producer}");

        var payload = await ReconcilePayloadAsync(world);
        payload.GetProperty("run_id").GetGuid().ShouldBe(world.RunId);
        payload.GetProperty("producer_id").GetString().ShouldBe(Producer);
        payload.GetProperty("declared_last_sequence").GetInt64().ShouldBe(3L);
        payload.GetProperty("declared_count").GetInt32().ShouldBe(3);
    }

    // ------------------------------------------------------------------ AC2: bound identity (T9/T88)

    /// <summary>
    /// 04.1.2 line 145's "finalize under foreign identity -> 403": a second token id and a second CI run reference
    /// both answer <c>producer_slot_taken</c> and change nothing — the slot stays open, no transition, no row.
    /// </summary>
    [Fact]
    public async Task Finalize_WrongIdentity_403ProducerSlotTaken()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");
        var runVersion = await RunVersionAsync(world);
        var body = await BodyAsync(world);

        // (a) another token id.
        await using (var foreign = HostFor(world, IngestTestWorld.Writer(jti: IngestTestWorld.OtherJti)))
        {
            using var foreignClient = foreign.CreateClient();
            var answer = await FinalizeAsync(foreignClient, world.RunId, body);

            answer.Status.ShouldBe(HttpStatusCode.Forbidden);
            answer.ErrorCode.ShouldBe("producer_slot_taken");
        }

        // (b) the bound token id claiming a DIFFERENT GitHub run: a different identity even for the same jti (T88).
        await using (var otherCi = HostFor(world, CiWriter(
            IngestTestWorld.Jti, projectScope: null, ciRun: new CiRunReference("github_actions", 42, 7, 1))))
        {
            using var otherCiClient = otherCi.CreateClient();
            var answer = await FinalizeAsync(otherCiClient, world.RunId, body);

            answer.Status.ShouldBe(HttpStatusCode.Forbidden);
            answer.ErrorCode.ShouldBe("producer_slot_taken");
        }

        // Neither refusal closed the slot or moved the run, and neither wrote an outbox row.
        (await SlotClosedAsync(world)).ShouldBeFalse();
        (await SlotDeclaresNothingAsync(world)).ShouldBeTrue();
        (await SlotBoundJtiAsync(world)).ShouldBe(IngestTestWorld.Jti);
        (await RunStateAsync(world)).ShouldBe("running");
        (await RunVersionAsync(world)).ShouldBe(runVersion);
        (await OutboxRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Finalize_UnknownProducer_422()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeAsync(
            world.Client, world.RunId, await BodyAsync(world, producerId: "shard-9", shard: 1));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("unknown_producer");
        (await SlotClosedAsync(world)).ShouldBeFalse();
        (await RunStateAsync(world)).ShouldBe("running");
        (await RunVersionAsync(world)).ShouldBe(runVersion);
        (await OutboxRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ Q2=A: the shard must be the slot's

    [Fact]
    public async Task Finalize_ShardMismatch_422_RequestInvalid_PointerShard()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");
        var runVersion = await RunVersionAsync(world);

        // shard-1 is the seeded slot at index 1; a body naming shard 2 is refused before the close.
        var answer = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, shard: 2));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        Details(answer).ShouldHaveSingleItem().GetProperty("path").GetString().ShouldBe("/shard");

        (await SlotClosedAsync(world)).ShouldBeFalse();
        (await RunStateAsync(world)).ShouldBe("running");
        (await RunVersionAsync(world)).ShouldBe(runVersion);
        (await OutboxRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC3: the manifest hash

    [Fact]
    public async Task Finalize_HashMismatch_422_ManifestInvalid_SlotStaysOpen_RunUnchanged()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");
        var runVersion = await RunVersionAsync(world);
        var expectedHash = await ManifestHashAsync(world);
        var body = await BodyAsync(world, manifestHash: WrongHash);

        var answer = await FinalizeAsync(world.Client, world.RunId, body);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("manifest_invalid");
        Details(answer).ShouldHaveSingleItem().GetProperty("path").GetString().ShouldBe("/manifest_hash");

        // The refusal never echoes the expected hash: the caller learns the comparison failed, nothing else.
        (answer.ErrorMessage ?? string.Empty).ShouldNotContain(expectedHash);
        (answer.Body.ToString()).ShouldNotContain(expectedHash);

        // Never evidence against the wrong plan (AC3): no close, no transition, no reconcile trigger.
        (await SlotClosedAsync(world)).ShouldBeFalse();
        (await SlotDeclaresNothingAsync(world)).ShouldBeTrue();
        (await RunStateAsync(world)).ShouldBe("running");
        (await RunVersionAsync(world)).ShouldBe(runVersion);
        (await OutboxRowsAsync(world)).ShouldBe(0);

        // The hash check runs AFTER the identity check, so a foreign caller sending the same wrong hash is 403 —
        // it cannot use the 422/403 difference to probe the run's manifest.
        await using var foreign = HostFor(world, IngestTestWorld.Writer(jti: IngestTestWorld.OtherJti));
        using var foreignClient = foreign.CreateClient();
        var foreignAnswer = await FinalizeAsync(foreignClient, world.RunId, body);

        foreignAnswer.Status.ShouldBe(HttpStatusCode.Forbidden);
        foreignAnswer.ErrorCode.ShouldBe("producer_slot_taken");

        // The foreign attempt neither re-bound nor closed the slot: the binding still names the original jti.
        (await SlotBoundJtiAsync(world)).ShouldBe(IngestTestWorld.Jti);
        (await SlotClosedAsync(world)).ShouldBeFalse();
        (await OutboxRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC6 / D3: duplicates

    [Fact]
    public async Task Finalize_Duplicate_SameValues_202_NoSecondTransition_OneOutboxRow()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");

        var first = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 3, count: 3));
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);

        var runVersion = await RunVersionAsync(world);
        var slotVersion = await SlotVersionAsync(world);

        // A resend with a DIFFERENT Idempotency-Key: producer_slot.closed_at is the arbiter (D3), not the key.
        var repeat = await FinalizeAsync(
            world.Client, world.RunId, await BodyAsync(world, lastSequence: 3, count: 3), NewKey());

        repeat.Status.ShouldBe(HttpStatusCode.Accepted, repeat.ErrorMessage);

        (await RunStateAsync(world)).ShouldBe("collecting");
        (await RunVersionAsync(world)).ShouldBe(runVersion, "a repeat finalize applies no transition");
        (await StateChangedRowsAsync(world, "collecting")).ShouldBe(1);
        (await StateChangedRowsAsync(world, "running")).ShouldBe(0);
        (await SlotVersionAsync(world)).ShouldBe(slotVersion, "the close is written once (T68)");
        (await SlotDeclaredLastSequenceAsync(world)).ShouldBe(3L);
        (await SlotDeclaredCountAsync(world)).ShouldBe(3);

        // One outbox row in total: no second reconcile trigger.
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Finalize_Repeat_DifferentValues_202_StoredValuesUnchanged()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");

        var first = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 3, count: 3));
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);

        var runVersion = await RunVersionAsync(world);
        var slotVersion = await SlotVersionAsync(world);

        // Q1=A (provisional): a differing repeat is STILL 202 — the first declaration wins and stays authoritative
        // for 05.3.2-T2. A 409 with a new wire code would be a contract change, so there is none.
        var repeat = await FinalizeAsync(
            world.Client, world.RunId, await BodyAsync(world, lastSequence: 9, count: 9), NewKey());

        repeat.Status.ShouldBe(HttpStatusCode.Accepted, repeat.ErrorMessage);
        (await SlotDeclaredLastSequenceAsync(world)).ShouldBe(3L);
        (await SlotDeclaredCountAsync(world)).ShouldBe(3);
        (await SlotVersionAsync(world)).ShouldBe(slotVersion);
        (await RunVersionAsync(world)).ShouldBe(runVersion);
        (await RunStateAsync(world)).ShouldBe("collecting");

        // The single trigger still carries the FIRST declaration, so the reconciler compares against what was stored.
        (await ReconcileRowsAsync(world)).ShouldBe(1);
        var payload = await ReconcilePayloadAsync(world);
        payload.GetProperty("declared_last_sequence").GetInt64().ShouldBe(3L);
        payload.GetProperty("declared_count").GetInt32().ShouldBe(3);
    }

    // ------------------------------------------------------------------ AC7: before the events, and shard 2

    [Fact]
    public async Task Finalize_ArrivesBeforeEvents_ClosesSlot_RunStaysCollecting()
    {
        // Two shards, neither bound yet: an accepted finalize performs the bind itself (BindOrCheckAsync), so a
        // finalize arriving before any event still closes the slot (05.3.2 §7, AC7).
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), slots: 2);
        await world.SetRunStateAsync("queued");
        var runVersion = await RunVersionAsync(world);

        var first = await FinalizeAsync(
            world.Client, world.RunId,
            await BodyAsync(world, producerId: "shard-1", shard: 1, lastSequence: 1, count: 1));

        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);

        // A still-queued run steps queued -> running -> collecting (Q4): queued -> collecting is an undeclared
        // forward-skip in RunStateMachine, so two edges are applied (and the row version moves twice).
        (await RunStateAsync(world)).ShouldBe("collecting");
        (await RunVersionAsync(world)).ShouldBe(runVersion + 2);
        (await StateChangedRowsAsync(world, "running")).ShouldBe(1);
        (await StateChangedRowsAsync(world, "collecting")).ShouldBe(1);

        (await SlotClosedAsync(world, "shard-1")).ShouldBeTrue();
        (await SlotBoundJtiAsync(world, "shard-1")).ShouldBe(IngestTestWorld.Jti);

        // shard 2 finalizes while the run is already collecting: closed, but no further transition.
        var second = await FinalizeAsync(
            world.Client, world.RunId,
            await BodyAsync(world, producerId: "shard-2", shard: 2, lastSequence: 1, count: 1));

        second.Status.ShouldBe(HttpStatusCode.Accepted, second.ErrorMessage);
        (await SlotClosedAsync(world, "shard-2")).ShouldBeTrue();
        (await RunStateAsync(world)).ShouldBe("collecting");
        (await RunVersionAsync(world)).ShouldBe(runVersion + 2, "a collecting run is already there");
        (await StateChangedRowsAsync(world, "collecting")).ShouldBe(1);

        // One trigger per newly closed slot: two shards, two rows.
        (await ReconcileRowsAsync(world)).ShouldBe(2);
    }

    // ------------------------------------------------------------------ T12: terminal runs are accepted and labelled

    [Theory]
    [InlineData("complete")]
    [InlineData("incomplete")]
    public async Task Finalize_TerminalRun_202_SlotClosed_NoStateChange(string state)
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync(state);
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world));

        // An accepted finalize on a terminal run closes the slot (T12 accept-and-mark) but changes no state.
        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await SlotClosedAsync(world)).ShouldBeTrue();
        (await RunStateAsync(world)).ShouldBe(state);
        (await RunVersionAsync(world)).ShouldBe(runVersion);
        (await StateChangedRowsAsync(world, state)).ShouldBe(0);
        (await StateChangedRowsAsync(world, "collecting")).ShouldBe(0);
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Finalize_CanceledRun_202_SlotClosed_NoStateChange()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("canceled");
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world));

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await SlotClosedAsync(world)).ShouldBeTrue();
        (await RunStateAsync(world)).ShouldBe("canceled");
        (await RunVersionAsync(world)).ShouldBe(runVersion);
        (await StateChangedRowsAsync(world, "canceled")).ShouldBe(0);
        (await StateChangedRowsAsync(world, "collecting")).ShouldBe(0);
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ 404: unknown and other tenant

    [Fact]
    public async Task Finalize_RunNotFound_404()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var answer = await FinalizeAsync(
            world.Client,
            Guid.CreateVersion7(),
            await BodyAsync(world, manifestHash: WrongHash));

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        (await OutboxRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Finalize_CrossTenant_404()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var (otherOrg, _) = await world.CreateOtherOrgAsync("globex");
        var otherRun = await world.SeedOtherRunAsync(otherOrg);

        // RLS hides the run: another tenant's run and an unknown id are the same answer (00 §5.4).
        var answer = await FinalizeAsync(
            world.Client, otherRun.RunId, await BodyAsync(world, manifestHash: WrongHash));

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        (await RunStateAsync(world, otherRun.RunId)).ShouldBe("created");
        (await OutboxRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ authorization boundaries

    [Fact]
    public async Task Finalize_MemberSession_Refused()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");
        var runVersion = await RunVersionAsync(world);

        // A real member session (the org's founder, resolved through the production tenant/principal path) does not
        // hold event:write — it is outside the tenant-assignable set (0010's catalogue) — so the pipeline refuses it.
        using var client = world.Api.CreateClient();
        var answer = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/runs/{world.RunId:D}/finalize",
            TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org),
            await BodyAsync(world),
            NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await SlotClosedAsync(world)).ShouldBeFalse();
        (await RunStateAsync(world)).ShouldBe("running");
        (await RunVersionAsync(world)).ShouldBe(runVersion);
        (await OutboxRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Finalize_TokenForOtherProject_403()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        using var setup = world.Api.CreateClient();
        var otherProject = await CreateProjectAsync(setup, world.User, world.Org, "other");
        var otherRun = await world.SeedRunInProjectAsync(otherProject);

        // The token is scoped to the world's first project; the run belongs to a sibling project of the same org.
        await using var scoped = HostFor(world, IngestTestWorld.Writer(projectScope: world.Project));
        using var scopedClient = scoped.CreateClient();

        var answer = await FinalizeAsync(
            scopedClient, otherRun.RunId, await BodyAsync(world, manifestHash: WrongHash));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("project_scope_mismatch");
        (await RunStateAsync(world, otherRun.RunId)).ShouldBe("created");
        (await OutboxRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ concurrency (AC6, lock order)

    [Fact]
    public async Task Finalize_ConcurrentSameSlot_OneCloseOneOutboxRow()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");
        var runVersion = await RunVersionAsync(world);
        var slotVersion = await SlotVersionAsync(world);

        // Two finalizes of the same slot at once, each declaring DIFFERENT values: one wins the close, the other is
        // the idempotent no-op that returns (and enqueues nothing for) the stored declaration.
        var answers = await Task.WhenAll(
            FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 4, count: 4)),
            FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 7, count: 7)));

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.Accepted);

        // Exactly one close write and exactly one applied transition (a second one would break the unique
        // outbox idempotency key or bump the version twice).
        (await SlotVersionAsync(world)).ShouldBe(slotVersion + 1);
        (await RunVersionAsync(world)).ShouldBe(runVersion + 1);
        (await StateChangedRowsAsync(world, "collecting")).ShouldBe(1);
        (await ReconcileRowsAsync(world)).ShouldBe(1);

        // The winner's declaration is what both reads see, and the single trigger carries the same values.
        var stored = await SlotDeclaredLastSequenceAsync(world);
        (stored == 4L || stored == 7L).ShouldBeTrue($"the close stored a declaration of one racer, not {stored}");
        (await ReconcilePayloadAsync(world)).GetProperty("declared_last_sequence").GetInt64().ShouldBe(stored);
    }

    [Fact]
    public async Task Finalize_ConcurrentWithEventBatch_NoDeadlock()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti);
        await world.SetRunStateAsync("running");

        // One event first, so the slot is bound and the run started; the race is the next finalize against the next
        // batch on the same run (05.3.1's lock-order rule: no FOR SHARE held while a transition is taken).
        var seeded = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));
        seeded.Status.ShouldBe(HttpStatusCode.OK, seeded.ErrorMessage);

        var answers = await Task.WhenAll(
            FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 2, count: 2)),
            IngestQaRequests.PostEventsAsync(
                world.Client,
                world.RunId,
                IngestQaRequests.Batch(IngestQaRequests.Event(
                    Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 2))));

        var finalize = answers[0];
        var batch = answers[1];

        // Neither side deadlocks (40P01) or fails with a 500, whatever order the two commit in.
        finalize.Status.ShouldBe(HttpStatusCode.Accepted, finalize.ErrorMessage);
        batch.Status.ShouldBe(HttpStatusCode.OK, batch.ErrorMessage);

        (await RunStateAsync(world)).ShouldBe("collecting");
        (await SlotClosedAsync(world)).ShouldBeTrue();
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }
}
