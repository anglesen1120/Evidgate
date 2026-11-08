// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The producer-slot facts of <c>GET /v1/runs/{r}</c>: one item per stored <c>producer_slot</c> row, in
/// <c>shard_index</c> order, carrying the slot's binding and finalize state and the counts of the event rows recorded
/// against it. Each test seeds the columns — the read has no writer for most of them — and asserts the served value
/// against the stored one, so a hard-coded or missing member fails. The binding values themselves (the token's id and
/// the CI run reference) must never reach the body.
/// </summary>
public sealed partial class GetRunDetailFieldsTests
{
    /// <summary>The bound token id every slot of a binding test stores; it must never appear in a body.</summary>
    private const string SlotJti = "jti-probe-secret-4f1";

    /// <summary>The CI run reference of the same tests; it must never appear in a body either.</summary>
    private const string SlotCiRunRef = "gh-run-probe-secret-9c2";

    /// <summary>
    /// The happy path: one run whose two slots are in different states, both served in the same body — a bound, still
    /// open slot with three recorded events of which one is stale, and a finalized slot that declared zero and recorded
    /// none. Every member of both items is asserted against the stored rows, so a count taken from the other slot's
    /// events or a finalize state read from the wrong row fails here.
    /// </summary>
    [Fact]
    public async Task GetRun_ProducerSlots_MixedStates_ComeFromTheStoredRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await NewRunAsync(world);
        await _fixture.ExecuteAsync(world.Database, $"""
            update public.producer_slot
               set bound_token_jti = '{SlotJti}', last_sequence = 4
             where run_id = '{runId:D}' and producer_id = 'shard-1'
            """);
        await _fixture.ExecuteAsync(world.Database, $"""
            update public.producer_slot
               set closed_at = now(), declared_last_sequence = 0, declared_count = 0
             where run_id = '{runId:D}' and producer_id = 'shard-2'
            """);
        await SeedEventAsync(world, runId, "shard-1", sequence: 1);
        await SeedEventAsync(world, runId, "shard-1", sequence: 2, stale: true);
        await SeedEventAsync(world, runId, "shard-1", sequence: 3);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var slots = SlotFacts(answer);
        slots.Select(item => item.GetProperty("producer_id").GetString()!).ShouldBe(["shard-1", "shard-2"]);

        var open = slots[0];
        open.GetProperty("identity_state").GetString().ShouldBe("bound");
        open.GetProperty("finalize_status").GetString().ShouldBe("open");
        open.GetProperty("last_sequence").GetInt64().ShouldBe(4);
        open.GetProperty("count").GetInt64().ShouldBe(3);
        open.GetProperty("expected").ValueKind.ShouldBe(JsonValueKind.Null, "an open slot declares nothing");
        open.GetProperty("stale_epoch_events").GetInt64().ShouldBe(1);
        open.GetProperty("after_cancel_events").GetInt64().ShouldBe(0);

        var finalized = slots[1];
        finalized.GetProperty("identity_state").GetString().ShouldBe("unbound");
        finalized.GetProperty("finalize_status").GetString().ShouldBe("finalized");
        finalized.GetProperty("last_sequence").GetInt64().ShouldBe(0);
        finalized.GetProperty("count").GetInt64().ShouldBe(0);
        finalized.GetProperty("expected").GetInt64().ShouldBe(0);
        finalized.GetProperty("stale_epoch_events").GetInt64().ShouldBe(0);
        finalized.GetProperty("after_cancel_events").GetInt64().ShouldBe(0);

        // The same values as the stored rows: last_sequence, declared count (null while open), finalize state, binding
        // state, and the slot's own event and stale counts — the last one is what a per-run instead of per-slot
        // aggregate would report as 3.
        (await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select concat_ws('|', p.producer_id, p.last_sequence, coalesce(p.declared_count::text, 'null'),
                             (p.closed_at is not null)::text, (p.bound_token_jti is not null)::text,
                             (select count(*) from public.event ev
                               where ev.org_id = p.org_id and ev.run_id = p.run_id and ev.producer_id = p.producer_id),
                             (select count(*) from public.event ev
                               where ev.org_id = p.org_id and ev.run_id = p.run_id and ev.producer_id = p.producer_id
                                 and ev.stale))
              from public.producer_slot p
             where p.run_id = '{runId:D}'
             order by p.shard_index
            """)).ShouldBe(["shard-1|4|null|false|true|3|1", "shard-2|0|0|true|false|0|0"]);
    }

    /// <summary>
    /// A slot whose token identity and CI run reference were written by the one bind reports <c>bound</c>, a slot
    /// with neither reports <c>unbound</c>, and neither binding value is served — only the state derived from them.
    /// </summary>
    [Fact]
    public async Task GetRun_ProducerSlots_TokenIdentityBoundsAndTheBindingIsNeverServed()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var manifest = RunCreationTests.Manifest(world.SuiteRevision, world.Cases, variants: 3, shards: 3);
        manifest["environment_revision_id"] = world.EnvironmentRevision.ToString();
        var manifestId = await RunCreationTests.UploadAsync(world.Client, world.Admin, world.Project, manifest);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(manifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        await _fixture.ExecuteAsync(world.Database, $"""
            update public.producer_slot set bound_token_jti = '{SlotJti}', bound_ci_run_ref = '{SlotCiRunRef}'
             where run_id = '{runId:D}' and producer_id = 'shard-1'
            """);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        SlotFacts(answer).Select(item => item.GetProperty("producer_id").GetString()!)
            .ShouldBe(["shard-1", "shard-2", "shard-3"]);
        SlotFacts(answer).Select(item => item.GetProperty("identity_state").GetString()!)
            .ShouldBe(["bound", "unbound", "unbound"]);
        answer.Body.GetRawText().ShouldNotContain(SlotJti, Case.Sensitive);
        answer.Body.GetRawText().ShouldNotContain(SlotCiRunRef, Case.Sensitive);
    }

    /// <summary>
    /// A finalized slot whose recorded rows disagree with its declared count is served as stored: the mismatch is the
    /// client's to derive, so both values are reported and neither is corrected.
    /// </summary>
    [Fact]
    public async Task GetRun_ProducerSlots_FinalizedCountMismatch_ServedAsStored()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await NewRunAsync(world);
        await _fixture.ExecuteAsync(world.Database, $"""
            update public.producer_slot
               set closed_at = now(), declared_last_sequence = 2, declared_count = 5
             where run_id = '{runId:D}' and producer_id = 'shard-1'
            """);
        await SeedEventAsync(world, runId, "shard-1", sequence: 1);
        await SeedEventAsync(world, runId, "shard-1", sequence: 2);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var slot = SlotFacts(answer)[0];
        slot.GetProperty("finalize_status").GetString().ShouldBe("finalized");
        slot.GetProperty("count").GetInt64().ShouldBe(2);
        slot.GetProperty("expected").GetInt64().ShouldBe(5);
        slot.GetProperty("expected").GetInt64().ShouldBe(
            await _fixture.ScalarAsync<int>(
                world.Database,
                $"select declared_count from public.producer_slot where run_id = '{runId:D}' and producer_id = 'shard-1'"));
    }

    /// <summary>A canceled run keeps accepting events; the slot that received one counts it beside its own events.</summary>
    [Fact]
    public async Task GetRun_ProducerSlots_AfterCancelEvent_CountedOnTheSlot()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await NewRunAsync(world);
        var canceled = await CancelRunTestData.CancelAsync(world, runId);
        canceled.Status.ShouldBe(HttpStatusCode.Accepted, canceled.ErrorMessage);
        await SeedEventAsync(world, runId, "shard-1", sequence: 1);
        await SeedEventAsync(world, runId, "shard-1", sequence: 2, afterCancel: true);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var slot = SlotFacts(answer)[0];
        slot.GetProperty("count").GetInt64().ShouldBe(2);
        slot.GetProperty("after_cancel_events").GetInt64().ShouldBe(1);
        slot.GetProperty("stale_epoch_events").GetInt64().ShouldBe(0);
    }

    /// <summary>
    /// A run that registered no slot reports an empty <c>producers</c> array — the member is present, not absent and
    /// not null, like <c>releases</c> on an unbound run.
    /// </summary>
    [Fact]
    public async Task GetRun_ProducerSlots_RunWithoutSlots_EmptyArray()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await NewRunAsync(world);
        await _fixture.ExecuteAsync(
            world.Database,
            $"delete from public.producer_slot where run_id = '{runId:D}'");

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("producers").ValueKind.ShouldBe(JsonValueKind.Array);
        answer.Body.GetProperty("producers").GetArrayLength().ShouldBe(0);
        answer.Body.GetProperty("declared_producers").GetArrayLength().ShouldBe(0);
        answer.Body.GetProperty("shard_count").GetInt32().ShouldBe(0);
    }

    /// <summary>
    /// Another tenant's run is the one <c>404 not_found</c>, and that body is the error envelope alone: the slots of the
    /// invisible run, and the binding they hold, are not reachable through the refusal.
    /// </summary>
    [Fact]
    public async Task GetRun_ProducerSlots_CrossTenant_404WithNoSlotLeak()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await NewRunAsync(world);
        await _fixture.ExecuteAsync(world.Database, $"""
            update public.producer_slot set bound_token_jti = '{SlotJti}'
             where run_id = '{runId:D}' and producer_id = 'shard-1'
            """);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));

        // The control: the owning tenant sees the run and its bound slot, so the 404 below is the tenant boundary.
        var own = await GetAsync(world, runId);
        own.Status.ShouldBe(HttpStatusCode.OK, own.ErrorMessage);
        SlotFacts(own)[0].GetProperty("identity_state").GetString().ShouldBe("bound");

        var foreign = await GetAsync(world, runId, TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg));

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe("not_found");
        foreign.Body.TryGetProperty("producers", out _).ShouldBeFalse();
        foreign.Body.GetRawText().ShouldNotContain(SlotJti, Case.Sensitive);
    }

    /// <summary>One API-created run of the world's manifest; returns its id.</summary>
    private static async Task<Guid> NewRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("run_id");
    }

    /// <summary>One stored event row of a slot, as the event table's bounds require it; the flags are the read's subject.</summary>
    private Task SeedEventAsync(
        RunCreationTests.World world,
        Guid runId,
        string producerId,
        long sequence,
        bool stale = false,
        bool afterCancel = false) =>
        _fixture.ExecuteAsync(world.Database, $"""
            insert into public.event
                (id, org_id, run_id, event_id, producer_id, sequence, execution_key, attempt_no, type, payload_hash,
                 stale, after_cancel)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{runId:D}', '{Guid.CreateVersion7()}', '{producerId}',
                    {sequence}, '{Guid.NewGuid():N}{Guid.NewGuid():N}', 1, 'test.step',
                    'sha256:{new string('a', 64)}', {stale}, {afterCancel})
            """);

    /// <summary>The <c>producers</c> items of a detail body, in the order the read reported them.</summary>
    private static List<JsonElement> SlotFacts(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("producers").EnumerateArray()];
}
