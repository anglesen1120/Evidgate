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

[Trait("Category", "Docker")]
public sealed partial class FinalizeEndpointTests : IClassFixture<TenancyHostPool>
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
    private readonly TenancyHostPool _hosts;

    public FinalizeEndpointTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

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
        _hosts.WithPrincipal(world.Database, world.Org, world.Project, principal);

    // ------------------------------------------------------------------ reads (superuser: scoped to this world's run)

    /// <summary>The run's manifest hash, through <c>run.manifest_id</c> (same tenant).</summary>
    private static Task<string> ManifestHashAsync(IngestTestWorld world, Guid? runId = null) =>
        world.ScalarAsync<string>(
            "select m.manifest_hash from public.manifest m "
            + $"join public.run r on r.org_id = m.org_id and r.manifest_id = m.id where r.id = '{runId ?? world.RunId:D}'");

    /// <summary>Moves any run of the world's tenant to another state (the concurrency cases seed extra runs).</summary>
    private Task SetRunStateAsync(IngestTestWorld world, Guid runId, string state) =>
        RunStateWalk.MoveAsync(_fixture, world.Database, runId, state);

    private static Task<bool> SlotClosedInRunAsync(IngestTestWorld world, Guid runId, string producerId = Producer) =>
        world.ScalarAsync<bool>(
            $"select closed_at is not null from public.producer_slot where run_id = '{runId:D}' and producer_id = '{producerId}'");

    /// <summary>The reconcile triggers written for one run (never for a refused or repeated finalize).</summary>
    private static Task<long> ReconcileRowsInRunAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync("outbox", $"task_type = '{ReconcileTaskType}' and aggregate_id = '{runId:D}'");

    private static Task<string> RunStateAsync(IngestTestWorld world, Guid? runId = null) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId ?? world.RunId}'");

    private static Task<int> RunVersionAsync(IngestTestWorld world, Guid? runId = null) =>
        world.ScalarAsync<int>($"select version from public.run where id = '{runId ?? world.RunId}'");

    /// <summary>
    /// The run's recorded state history as one delimited scalar, in sequence order; the creation row reads
    /// <c>(insert)-&gt;x</c>. The trigger on <c>public.run</c> is its only writer (migration 0082).
    /// </summary>
    private static Task<string> StateHistoryAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>(
            "select coalesce(string_agg(coalesce(from_state, '(insert)') || '->' || to_state, ',' order by seq), '') "
            + $"from public.run_state_event where org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'");

    private static Task<bool> SlotClosedAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<bool>(
            $"select closed_at is not null from public.producer_slot where run_id = '{world.RunId:D}' and producer_id = '{producerId}'");

    /// <summary>The stored declared high-water mark; <c>-1</c> while the slot is open (the column is NULL then).</summary>
    private static Task<long> SlotDeclaredLastSequenceAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<long>(
            $"select coalesce(declared_last_sequence, -1) from public.producer_slot where run_id = '{world.RunId:D}' and producer_id = '{producerId}'");

    /// <summary>The stored declared count; <c>-1</c> while the slot is open (the column is NULL then).</summary>
    private static Task<int> SlotDeclaredCountAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<int>(
            $"select coalesce(declared_count, -1) from public.producer_slot where run_id = '{world.RunId:D}' and producer_id = '{producerId}'");

    /// <summary>True while the slot declares nothing — the open state migration 0035's all-or-nothing CHECK allows.</summary>
    private static Task<bool> SlotDeclaresNothingAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<bool>(
            "select declared_last_sequence is null and declared_count is null "
            + $"from public.producer_slot where run_id = '{world.RunId:D}' and producer_id = '{producerId}'");

    private static Task<int> SlotVersionAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<int>(
            $"select version from public.producer_slot where run_id = '{world.RunId:D}' and producer_id = '{producerId}'");

    /// <summary>The bound token id; empty while the slot is unbound.</summary>
    private static Task<string> SlotBoundJtiAsync(IngestTestWorld world, string producerId = Producer) =>
        world.ScalarAsync<string>(
            $"select coalesce(bound_token_jti, '') from public.producer_slot where run_id = '{world.RunId:D}' and producer_id = '{producerId}'");

    /// <summary>The reconcile triggers written for the world's run (never for a refused or repeated finalize).</summary>
    private static Task<long> ReconcileRowsAsync(IngestTestWorld world) =>
        world.CountAsync("outbox", $"task_type = '{ReconcileTaskType}' and aggregate_id = '{world.RunId:D}'");

    /// <summary>The applied transitions into <paramref name="to"/> for the world's run.</summary>
    private static Task<long> StateChangedRowsAsync(IngestTestWorld world, string to) =>
        world.CountAsync(
            "outbox",
            $"task_type = '{StateChangedTaskType}' and aggregate_id = '{world.RunId:D}' and payload->>'to' = '{to}'");

    /// <summary>Every outbox row of the test's tenant: a refusal writes none at all.</summary>
    private static Task<long> OutboxRowsAsync(IngestTestWorld world) =>
        world.CountAsync("outbox", $"org_id = '{world.Org:D}'");

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

}
