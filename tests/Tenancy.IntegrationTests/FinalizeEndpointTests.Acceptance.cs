using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class FinalizeEndpointTests
{
    // ------------------------------------------------------------------ AC1: the accepted finalize

    [Fact]
    public async Task Finalize_BoundIdentity_ManifestHashMatches_202_SlotClosed_RunCollecting()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
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
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
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
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
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
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
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
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
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
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
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
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        await world.SetRunStateAsync("running");

        var first = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 3, count: 3));
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);

        var runVersion = await RunVersionAsync(world);
        var slotVersion = await SlotVersionAsync(world);

        // Q1=A (decided by the coordinator, owner rule 2026-10-23): a differing repeat is STILL 202 — the first
        // declaration wins and stays authoritative for 05.3.2-T2. A 409 with a new wire code would be a contract
        // change, so there is none.
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
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), slots: 2, hosts: _hosts);
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

        // Each edge also appends its history row, in order: the trigger on public.run is the only writer, so the
        // statement that moves the state is the statement that records it (migration 0082).
        (await StateHistoryAsync(world)).ShouldEndWith("queued->running,running->collecting");

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
        // A finalize that applies no edge appends no history row: the history is still the two edges already applied.
        (await StateHistoryAsync(world)).ShouldEndWith("queued->running,running->collecting");

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
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
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
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
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
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);

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
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        var (otherOrg, _) = await world.CreateOtherOrgAsync(Unique.Slug("globex"));
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
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
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
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
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

}
