using Evidgate.Migrator;
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
/// PostgreSQL 18 container per assembly, a database brought to head by the real
/// <c>Evidgate.Migrator</c> — the 05.2.1-T1 / 05.3.3-T1 precedent for a new table in this suite.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EventSchemaTests
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
    private async Task<World> StartAsync(string? database = null)
    {
        database ??= await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>An org and a project in it, created through the real routes.</summary>
    private static async Task<Guid> CreateOtherTenantAsync(World world, string slug)
    {
        // The database is shared, so the slug must be unique to this test's org.
        var unique = Unique.Slug(slug);
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, unique);
        await CreateProjectAsync(world.Client, user, org, unique + "-project");

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
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.TableExistsAsync(database, "event")).ShouldBeTrue();
        (await _fixture.TableExistsAsync(database, "event_batch")).ShouldBeTrue();
        (await _fixture.RowSecurityAsync(database, "event")).ShouldBe((true, true));
        (await _fixture.RowSecurityAsync(database, "event_batch")).ShouldBe((true, true));
    }

    [Fact]
    public async Task TenantPolicies_AreScopedToTheAppRole()
    {
        var database = await _fixture.SharedDatabaseAsync();

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
        var database = await _fixture.SharedDatabaseAsync();

        var eventConstraints = await _fixture.ConstraintNamesAsync(database, "event");
        foreach (var name in new[]
                 {
                     "event_pkey", "event_org_id_key", "event_run_producer_sequence_key", "event_run_event_id_key",
                     // No `event_run_fk`: the direct run key is deliberately absent (0034's header
                     // [DECISION]); `event_slot_fk` plus producer_slot's validated run key already
                     // make a dangling or cross-org run impossible.
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
        var database = await _fixture.SharedDatabaseAsync();

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

}
