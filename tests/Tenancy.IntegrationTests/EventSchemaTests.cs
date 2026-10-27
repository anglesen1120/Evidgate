using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.1.1-T1 through a real PostgreSQL: migration 0034's <c>event</c> and <c>event_batch</c> (FORCE
/// RLS, the composite tenant keys <c>event_slot_fk</c>/<c>event_batch_run_fk</c>,
/// the append-only grants, the bound CHECKs and the <c>event_batch_run</c> index) plus the
/// <c>producer_slot.last_sequence</c> column grant the 0029 header handed to this task.
/// <para>
/// The fixture is this project's own <see cref="PostgresTenancyFixture"/> — one digest-pinned
/// PostgreSQL 18 container per assembly, a fresh database per test brought to head by the real
/// <c>Evidgate.Migrator</c> — the 05.2.1-T1 / 05.3.3-T1 precedent for a new table in this suite.
/// </para>
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class EventSchemaTests
{
    /// <summary>The migration under test, re-run by the existing-row-safety case.</summary>
    private const string MigrationFile = "0034_ingestion_event_and_batch.sql";

    /// <summary>The manifest-derived producer id 05's run-create registers (T54).</summary>
    private const string ProducerId = "shard-1";

    /// <summary>A well-formed execution key: the 00 §7 sha256 hex.</summary>
    private static readonly string ExecutionKey = new('a', 64);

    /// <summary>A well-formed payload hash: the 00 §7 RFC 8785 content address.</summary>
    private static readonly string PayloadHash = "sha256:" + new string('b', 64);

    private readonly PostgresTenancyFixture _fixture;

    public EventSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One migrated database with an org and a project, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>An org and a project in it, created through the real routes.</summary>
    private static async Task<Guid> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        await CreateProjectAsync(world.Client, user, org, slug + "-project");

        return org;
    }

    /// <summary>A committed run for one tenant, written as the migration role (05.2.1-T1's seeder).</summary>
    private async Task<Guid> SeedRunAsync(World world, Guid org) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, org);

    /// <summary>One registered producer slot for the run, written as the app role (04.1.2-T4's shape).</summary>
    private async Task SeedSlotAsync(World world, Guid org, Guid runId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        (await scope.ExecuteAsync(InsertSlotSql(org, runId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>One committed event row and its batch receipt, written as the app role.</summary>
    private async Task SeedEventAsync(World world, Guid org, Guid runId, Guid? eventId = null, long sequence = 1)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        (await scope.ExecuteAsync(InsertEventSql(org, runId, eventId, sequence: sequence))).ShouldBe(1);
        (await scope.ExecuteAsync(InsertEventBatchSql(org, runId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>Runs one statement as the app role inside a fresh tenant scope and returns the server error.</summary>
    private async Task<PostgresException> CaptureAppErrorAsync(World world, Guid org, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>One raw insert of a producer slot as the app role.</summary>
    private static string InsertSlotSql(Guid org, Guid runId) =>
        "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id) "
        + $"values ('{Guid.CreateVersion7()}', '{org}', '{runId}', 1, '{ProducerId}')";

    /// <summary>One raw insert of an event row as the app role; every bounded column is overridable.</summary>
    private static string InsertEventSql(
        Guid org,
        Guid runId,
        Guid? eventId = null,
        string producerId = ProducerId,
        long sequence = 1,
        string? executionKey = null,
        int attemptNo = 1,
        string type = "test.step",
        string? payloadHash = null) =>
        "insert into public.event "
        + "(id, org_id, run_id, event_id, producer_id, sequence, execution_key, attempt_no, type, payload_hash) "
        + $"values ('{Guid.CreateVersion7()}', '{org}', '{runId}', '{eventId ?? Guid.CreateVersion7()}', "
        + $"'{producerId}', {sequence}, '{executionKey ?? ExecutionKey}', {attemptNo}, '{type}', '{payloadHash ?? PayloadHash}')";

    /// <summary>One raw insert of a batch receipt as the app role.</summary>
    private static string InsertEventBatchSql(Guid org, Guid runId) =>
        "insert into public.event_batch "
        + "(id, org_id, run_id, producer_id, event_count, bytes_in, bytes_expanded, accepted, duplicate, request_id) "
        + $"values ('{Guid.CreateVersion7()}', '{org}', '{runId}', '{ProducerId}', 1, 256, 256, 1, 0, 'probe-request')";

    /// <summary>The migration file's own SQL, executed as the migration role — the re-run path.</summary>
    private async Task ReRunMigrationAsync(string database)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(await MigrationSqlAsync());
        await scope.CommitAsync();
    }

    private static Task<string> MigrationSqlAsync() =>
        File.ReadAllTextAsync(
            Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, MigrationFile),
            TestContext.Current.CancellationToken);

    // ------------------------------------------------------------------ (a) the migration: shape, RLS, grants

    [Fact]
    public async Task EventAndEventBatch_HaveForcedRls()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.TableExistsAsync(database, "event")).ShouldBeTrue();
        (await _fixture.TableExistsAsync(database, "event_batch")).ShouldBeTrue();
        (await _fixture.RowSecurityAsync(database, "event")).ShouldBe((true, true));
        (await _fixture.RowSecurityAsync(database, "event_batch")).ShouldBe((true, true));
    }

    [Fact]
    public async Task TenantPolicies_AreScopedToTheAppRole()
    {
        var database = await _fixture.CreateDatabaseAsync();

        // A policy with no `to` clause applies to PUBLIC; the tenant policy must name evidgate_app only,
        // so the operator's SELECT grant reads zero rows (T76).
        (await _fixture.PoliciesAsync(database, "event")).ShouldBe(
        [
            "event_owner_backfill:evidgate_owner:*:true:true",
            "event_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
        ]);
        (await _fixture.PoliciesAsync(database, "event_batch")).ShouldBe(
        [
            "event_batch_owner_backfill:evidgate_owner:*:true:true",
            "event_batch_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
        ]);
    }

    [Fact]
    public async Task EventAndEventBatch_ConstraintsAndIndex_Exist()
    {
        var database = await _fixture.CreateDatabaseAsync();

        var eventConstraints = await _fixture.ConstraintNamesAsync(database, "event");
        foreach (var name in new[]
                 {
                     "event_pkey", "event_org_id_key", "event_run_producer_sequence_key", "event_run_event_id_key",
                     // No `event_run_fk`: the task file §4 defers the direct run key to 05.2.1 (0034's
                     // header [DECISION]); `event_slot_fk` plus producer_slot's validated run key
                     // already make a dangling or cross-org run impossible.
                     "event_slot_fk",
                     "event_producer_id_check", "event_sequence_check", "event_epoch_check",
                     "event_execution_key_check", "event_attempt_no_check", "event_type_check",
                     "event_raw_status_check", "event_normalized_status_check", "event_duration_ms_check",
                     "event_payload_hash_check", "event_payload_check",
                 })
        {
            eventConstraints.Any(entry => entry.EndsWith(":" + name, StringComparison.Ordinal)).ShouldBeTrue(name);
        }

        var batchConstraints = await _fixture.ConstraintNamesAsync(database, "event_batch");
        foreach (var name in new[]
                 {
                     "event_batch_pkey", "event_batch_org_id_key", "event_batch_run_fk",
                     "event_batch_producer_id_check", "event_batch_event_count_check", "event_batch_bytes_in_check",
                     "event_batch_bytes_expanded_check", "event_batch_accepted_check",
                     "event_batch_duplicate_check", "event_batch_request_id_check",
                 })
        {
            batchConstraints.Any(entry => entry.EndsWith(":" + name, StringComparison.Ordinal)).ShouldBeTrue(name);
        }

        (await _fixture.IndexesAsync(database, "event_batch"))
            .Any(index => index.StartsWith("event_batch_run:", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task AppRole_HasOnlySelectInsert_OnEventAndEventBatch()
    {
        var database = await _fixture.CreateDatabaseAsync();

        foreach (var table in new[] { "event", "event_batch" })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "select")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "insert")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "update")).ShouldBeFalse(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "delete")).ShouldBeFalse(table);

            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", table, "select")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_worker_sched", table, "select")).ShouldBeFalse(table);
        }
    }

    // ------------------------------------------------------------------ (b) the composite tenant keys

    [Fact]
    public async Task Event_WithoutRegisteredSlot_FailsSlotFk()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);

        // The run exists but no producer_slot was registered for it (04.1.2-T4), so the 0029 hand-over
        // key is the only key the row can violate.
        var error = await CaptureAppErrorAsync(world, world.Org, InsertEventSql(world.Org, runId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("event_slot_fk");

        // Positive control: with the slot registered the same row is accepted, so the refusal is the
        // missing slot and not a bad seed.
        await SeedSlotAsync(world, world.Org, runId);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertEventSql(world.Org, runId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    [Fact]
    public async Task Event_UnknownRun_FailsSlotFk()
    {
        await using var world = await StartAsync();
        var unknownRun = Guid.CreateVersion7();

        // A run that does not exist has no registered slot either, so `event_slot_fk` is the key the
        // row violates: the run is guaranteed transitively (`event_slot_fk` -> `producer_slot` ->
        // 0031's validated `producer_slot_run_fk` -> `run`), because the task file §4 defers the
        // direct `event_run_fk` to 05.2.1 (0034's header [DECISION]).
        var error = await CaptureAppErrorAsync(world, world.Org, InsertEventSql(world.Org, unknownRun));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("event_slot_fk");
    }

    [Fact]
    public async Task EventBatch_UnknownRun_FailsRunFk()
    {
        await using var world = await StartAsync();
        var unknownRun = Guid.CreateVersion7();

        var error = await CaptureAppErrorAsync(world, world.Org, InsertEventBatchSql(world.Org, unknownRun));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("event_batch_run_fk");
    }

    // ------------------------------------------------------------------ (c) append-only: no UPDATE, no DELETE

    [Fact]
    public async Task AppRole_CannotUpdateOrDelete_EventAndEventBatch()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);
        await SeedSlotAsync(world, world.Org, runId);
        await SeedEventAsync(world, world.Org, runId);

        (await CaptureAppErrorAsync(world, world.Org, "update public.event set late = true")).SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "delete from public.event")).SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "update public.event_batch set accepted = 0")).SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "delete from public.event_batch")).SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ (d) RLS behaviour

    [Fact]
    public async Task EventRows_AreInvisibleWithoutTheTenantAndAcrossOrgs()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);
        await SeedSlotAsync(world, world.Org, runId);
        await SeedEventAsync(world, world.Org, runId);
        var otherOrg = await CreateOtherTenantAsync(world, "beta");

        // 1. No tenant setting: the policy reads NULL and both tables are empty.
        await using (var unset = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database)))
        {
            (await unset.ScalarAsync<long>("select count(*) from public.event")).ShouldBe(0);
            (await unset.ScalarAsync<long>("select count(*) from public.event_batch")).ShouldBe(0);
        }

        // 2. Under the owning tenant the seeded rows are visible, so the zeros above are the policy's doing.
        await using (var mine = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await mine.ScalarAsync<long>("select count(*) from public.event")).ShouldBe(1);
            (await mine.ScalarAsync<long>("select count(*) from public.event_batch")).ShouldBe(1);
        }

        // 3. Under the other org they are invisible.
        await using (var theirs = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await theirs.ScalarAsync<long>("select count(*) from public.event")).ShouldBe(0);
            (await theirs.ScalarAsync<long>("select count(*) from public.event_batch")).ShouldBe(0);
        }
    }

    // ------------------------------------------------------------------ (e) the two unique keys

    [Fact]
    public async Task DuplicateEventId_FailsTheRunEventIdKey()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);
        await SeedSlotAsync(world, world.Org, runId);
        var eventId = Guid.CreateVersion7();
        await SeedEventAsync(world, world.Org, runId, eventId);

        // The same event_id under a different sequence: the T50 dedupe conflict target is the only key
        // that can reject it.
        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertEventSql(world.Org, runId, eventId, sequence: 2));

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("event_run_event_id_key");
    }

    [Fact]
    public async Task DuplicateProducerSequence_FailsTheRunProducerSequenceKey()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);
        await SeedSlotAsync(world, world.Org, runId);
        await SeedEventAsync(world, world.Org, runId);

        // A different event_id at the same (producer, sequence): the FR-04-3 ordering key is the only key
        // that can reject it.
        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertEventSql(world.Org, runId, sequence: 1));

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("event_run_producer_sequence_key");
    }

    // ------------------------------------------------------------------ (f) the CHECK boundaries

    [Fact]
    public async Task EventType_OutsideTheEnvelopeShape_FailsCheck()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);
        await SeedSlotAsync(world, world.Org, runId);

        // The envelope's `type` is a dotted lower_snake token; the closed five-value vocabulary is the
        // handler's (D1), the shape is the database's.
        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertEventSql(world.Org, runId, type: "Test.Started"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("event_type_check");
    }

    [Fact]
    public async Task PayloadHash_WithoutTheContentAddressPrefix_FailsCheck()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);
        await SeedSlotAsync(world, world.Org, runId);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertEventSql(world.Org, runId, payloadHash: new string('b', 64)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("event_payload_hash_check");
    }

    // ------------------------------------------------------------------ (g) the producer_slot.last_sequence grant

    [Fact]
    public async Task AppRole_MayUpdateOnlyLastSequence_OnProducerSlot()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);
        await SeedSlotAsync(world, world.Org, runId);

        // The ingest handler's slot write names exactly this column (0034, the 0029 hand-over).
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(
                $"update public.producer_slot set last_sequence = 7 where org_id = '{world.Org}' and run_id = '{runId}'"))
                .ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select last_sequence from public.producer_slot")).ShouldBe(7);

        // Every other column stays outside the app role's grants: `closed_at` and the two declared values
        // are 0035's (04.1.2-T5's `CloseSlotAsync` names exactly those three columns), `version` is the
        // shared trigger's, and a slot is never deleted.
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'closed_at', 'update')")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'declared_last_sequence', 'update')")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'declared_count', 'update')")).ShouldBeTrue();
        (await CaptureAppErrorAsync(world, world.Org, "update public.producer_slot set producer_id = 'shard-9'"))
            .SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "update public.producer_slot set version = 9"))
            .SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "delete from public.producer_slot")).SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ (g2) the run row lock grant (T91)

    [Fact]
    public async Task RunRow_CanBeLockedForShare_ByAppRole()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);

        // T91: the ingest handler reads the run row `FOR SHARE` inside the ingest transaction, and
        // PostgreSQL requires UPDATE on at least one column of every table named in a locking clause
        // (`FOR SHARE` included). 0031 revoked table-level UPDATE on `run`, so 0034 grants
        // `updated_at` — the column the shared version trigger assigns — and that grant is what makes
        // this lock legal rather than 42501.
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<Guid>(
            $"select id from public.run where org_id = '{world.Org}' and id = '{runId}' for share")).ShouldBe(runId);
        await scope.CommitAsync();
    }

    [Fact]
    public async Task RunState_IsTheGuardedUpdateGrant()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);

        // 0034 granted `updated_at` (the T91 row lock), 05.3.1's 0036 granted `state` (the guarded
        // transition) and 05.3.2-T5's 0049 granted `current_evidence_revision_id` (its own guarded move,
        // covered by EvidenceRevisionSchemaTests), so an un-granted column of `run` stays 42501 — `purpose`
        // is the example here.
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_column_privilege('evidgate_app', 'public.run', 'updated_at', 'update')")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_column_privilege('evidgate_app', 'public.run', 'state', 'update')")).ShouldBeTrue();

        (await CaptureAppErrorAsync(world, world.Org, $"update public.run set purpose = 'replay' where id = '{runId}'"))
            .SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ (h) the migration re-run

    [Fact]
    public async Task Migration_ReRun_KeepsAValidEventRow()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);
        await SeedSlotAsync(world, world.Org, runId);
        await SeedEventAsync(world, world.Org, runId);

        // The file is idempotent: the tables, policies, grants and the column grant already exist and
        // every statement is guarded — the rows survive and the grant is still in place.
        await ReRunMigrationAsync(world.Database);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.event")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.event_batch")).ShouldBe(1);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'last_sequence', 'update')"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_column_privilege('evidgate_app', 'public.run', 'updated_at', 'update')"))
            .ShouldBeTrue();
    }
}
