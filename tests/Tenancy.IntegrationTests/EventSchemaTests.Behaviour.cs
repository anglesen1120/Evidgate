using Evidgate.Migrator;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The behaviour half of <see cref="EventSchemaTests"/>: the composite tenant keys, the append-only
/// grants, RLS isolation, the two unique keys, the CHECK boundaries and the <c>producer_slot</c> and
/// <c>run</c> column grants, all exercised by writing rows in the test's own tenant.
/// </summary>
public sealed partial class EventSchemaTests
{
    // ------------------------------------------------------------------ (b) the composite tenant keys

    [Fact]
    public async Task Event_WithoutRegisteredSlot_FailsSlotFk()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);

        // The run exists but no producer_slot was registered for it, so the 0029 hand-over key is the
        // only key the row can violate.
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

        // The same event_id under a different sequence: the dedupe conflict target is the only key that
        // can reject it.
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

        // A different event_id at the same (producer, sequence): the ordering key is the only key that
        // can reject it.
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

        (await _fixture.ScalarAsync<long>(world.Database, $"select last_sequence from public.producer_slot where run_id = '{runId}'")).ShouldBe(7);

        // Every other column stays outside the app role's grants: `closed_at` and the two declared values
        // are 0035's (`CloseSlotAsync` names exactly those three columns), `version` is the shared
        // trigger's, and a slot is never deleted.
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

    // ------------------------------------------------------------------ (g2) the run row lock grant

    [Fact]
    public async Task RunRow_CanBeLockedForShare_ByAppRole()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world, world.Org);

        // The ingest handler reads the run row `FOR SHARE` inside the ingest transaction, and
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

        // 0034 granted `updated_at` (the row lock), 0036 granted `state` (the guarded transition) and
        // 0049 granted `current_evidence_revision_id` (its own guarded move, covered by
        // EvidenceRevisionSchemaTests), so an un-granted column of `run` stays 42501 — `purpose` is the
        // example here.
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
        // The file drops and recreates policies and grants, so the re-run needs a database of its own.
        await using var dedicated = await _fixture.CreateEmptyDedicatedDatabaseAsync();
        var migrated = _fixture.ApplyMigrations(dedicated.Name);
        migrated.ExitCode.ShouldBe(MigratorExitCodes.Success, migrated.Transcript);
        await using var world = await StartAsync(dedicated.Name);
        var runId = await SeedRunAsync(world, world.Org);
        await SeedSlotAsync(world, world.Org, runId);
        await SeedEventAsync(world, world.Org, runId);

        // The file is idempotent: the tables, policies, grants and the column grant already exist and
        // every statement is guarded — the rows survive and the grant is still in place.
        await ReRunMigrationAsync(world.Database);

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.event where org_id = '{world.Org}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.event_batch where org_id = '{world.Org}'")).ShouldBe(1);
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
