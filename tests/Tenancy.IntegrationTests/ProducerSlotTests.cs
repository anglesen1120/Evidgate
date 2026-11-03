using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Domain;
using Evidgate.Modules.Ingestion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.1.2-T4 through a real PostgreSQL: migration 0029's <c>producer_slot</c> table (FORCE RLS, the
/// tenant key and the two run-scoped uniques, the app-role grants and the bound CHECKs) and the
/// <see cref="IProducerRegistry"/> contract - the per-shard registration inside a caller's tenant
/// transaction (AC2) and the first-write binding (T9). The fixture is this project's own
/// <see cref="PostgresTenancyFixture"/> (one digest-pinned PostgreSQL 18 container per assembly, a
/// fresh database per test brought to head by the real <c>Evidgate.Migrator</c>), the 06.1.1-T1
/// precedent for a new table in this suite.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ProducerSlotTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ProducerSlotTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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

    /// <summary>One raw insert of a slot row as the app role; every bounded column is overridable.</summary>
    private static string InsertSlotSql(
        Guid org,
        Guid runId,
        int shardIndex = 1,
        string producerId = "shard-1",
        string? boundTokenJti = null,
        string? boundCiRunRef = null,
        long lastSequence = 0) =>
        "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti, bound_ci_run_ref, last_sequence) "
        + $"values ('{Guid.CreateVersion7()}', '{org}', '{runId}', {shardIndex}, '{producerId}', "
        + $"{(boundTokenJti is null ? "null" : $"'{boundTokenJti}'")}, {(boundCiRunRef is null ? "null" : $"'{boundCiRunRef}'")}, {lastSequence})";

    /// <summary>One committed slot row for <paramref name="world"/>'s org, written as the app role.</summary>
    private async Task InsertSlotAsync(World world, Guid runId, int shardIndex = 1, string producerId = "shard-1")
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertSlotSql(world.Org, runId, shardIndex, producerId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>One committed run row for <paramref name="world"/>'s org (0031's composite run key).</summary>
    private async Task<Guid> CreateRunAsync(World world) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

    /// <summary>One committed run row for <paramref name="org"/> (0031's composite run key).</summary>
    private async Task<Guid> CreateRunAsync(World world, Guid org) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, org);

    /// <summary>An org and a project in it, created through the real routes.</summary>
    private static async Task<Guid> CreateOtherOrgAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        await CreateProjectAsync(world.Client, user, org, slug + "-project");

        return org;
    }

    /// <summary>Runs one registry call inside a fresh ambient tenant scope for <paramref name="org"/>.</summary>
    private static async Task<T> InScopeAsync<T>(
        World world,
        Guid org,
        Func<IProducerRegistry, ValueTask<T>> call,
        bool commit = true)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var result = await call(services.ServiceProvider.GetRequiredService<IProducerRegistry>());

        if (commit)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    /// <summary>The slot count of one org, read as the app role inside a tenant scope.</summary>
    private async Task<long> CountAsync(World world, Guid org)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.ScalarAsync<long>("select count(*) from public.producer_slot");
    }

    // ------------------------------------------------------------------ (a) migration: RLS flags

    [Fact]
    public async Task ProducerSlot_HasForcedRls()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.RowSecurityAsync(database, "producer_slot")).ShouldBe((true, true));
    }

    [Fact]
    public async Task TenantPolicies_AreScopedToTheAppRole()
    {
        var database = await _fixture.CreateDatabaseAsync();

        // A policy with no `to` clause applies to PUBLIC; the tenant policy must name evidgate_app
        // only, so the operator's SELECT grant reads zero rows (T76).
        (await _fixture.PoliciesAsync(database, "producer_slot")).ShouldBe(
        [
            "producer_slot_owner_backfill:evidgate_owner:*:true:true",
            "producer_slot_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
        ]);
    }

    [Fact]
    public async Task TenantKey_AndRunScopedUniques_Exist()
    {
        var database = await _fixture.CreateDatabaseAsync();

        var constraints = await _fixture.ConstraintNamesAsync(database, "producer_slot");

        // Entries are "type:name" (pg_constraint.contype), so each expected name is matched on its name part.
        foreach (var name in new[] { "producer_slot_pkey", "producer_slot_org_id_key", "producer_slot_run_producer_key", "producer_slot_run_shard_key" })
        {
            constraints.Any(entry => entry.EndsWith(":" + name, StringComparison.Ordinal)).ShouldBeTrue(name);
        }
    }

    [Fact]
    public async Task RunIndex_ExistsInPgIndexes()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.IndexesAsync(database, "producer_slot")).Any(index => index.StartsWith("producer_slot_run:", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeclaredColumns_ChecksAndUpdateGrants_Exist()
    {
        var database = await _fixture.CreateDatabaseAsync();

        // Migration 0035 (04.1.2-T5) adds README T68's two declared-values columns as nullable: an open
        // slot declares nothing, and `CloseSlotAsync` writes them once with `closed_at`. The shape is the
        // 00 §7 column contract - bigint/int, no default, `not null` absent (is_nullable = YES).
        var columns = await _fixture.ColumnsAsync(database, "producer_slot");
        columns.ShouldContain("declared_last_sequence:bigint:int8:YES:-");
        columns.ShouldContain("declared_count:integer:int4:YES:-");

        // Both CHECKs mirror the column they guard, exactly like 0029's `producer_slot_last_sequence_check`:
        // `declared_last_sequence >= 0` for the high-water mark and the same non-negative bound for the count.
        var constraints = await _fixture.ConstraintsAsync(database, "producer_slot");
        constraints.ShouldContain(
            constraint => constraint.StartsWith("c:producer_slot_declared_last_sequence_check:", StringComparison.Ordinal)
                && constraint.Contains(">= 0", StringComparison.Ordinal),
            "declared_last_sequence needs its non-negative CHECK (0035)");
        constraints.ShouldContain(
            constraint => constraint.StartsWith("c:producer_slot_declared_count_check:", StringComparison.Ordinal)
                && constraint.Contains(">= 0", StringComparison.Ordinal),
            "declared_count needs its non-negative CHECK (0035)");

        // The all-or-nothing CHECK (Sec L2): `closed_at` and the two declared values are written together by
        // CloseSlotAsync's single UPDATE, so a row carries all three or none. It is the pair-null idiom of
        // 0008's `task_fire_pair_check` repeated for the three columns.
        constraints.ShouldContain(
            constraint => constraint.StartsWith("c:producer_slot_closed_declared_check:", StringComparison.Ordinal)
                && constraint.Contains("IS NULL", StringComparison.Ordinal),
            "the closed_at and declared-value columns need their all-or-nothing CHECK (0035)");

        // The UPDATE grants the app role holds on the table are exactly the columns the module's writes
        // name: the bind's two (0029), the ingest handler's `last_sequence` (0034) and `CloseSlotAsync`'s
        // three (0035: `closed_at` plus the two declared values). Every other column stays outside the grant.
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select column_name
              from information_schema.columns
             where table_schema = 'public' and table_name = 'producer_slot'
               and has_column_privilege('evidgate_app', 'public.producer_slot', column_name, 'UPDATE')
             order by column_name
            """))
            .ShouldBe(
            [
                "bound_ci_run_ref",
                "bound_token_jti",
                "closed_at",
                "declared_count",
                "declared_last_sequence",
                "last_sequence",
            ]);
    }

    // ------------------------------------------------------------------ (b) RLS behaviour

    [Fact]
    public async Task Select_WithoutTenantContext_ReturnsZero()
    {
        await using var world = await StartAsync();
        await InsertSlotAsync(world, await CreateRunAsync(world));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));

        (await scope.ScalarAsync<long>("select count(*) from public.producer_slot")).ShouldBe(0);
    }

    [Fact]
    public async Task Select_OtherOrg_ReturnsZero()
    {
        await using var world = await StartAsync();
        await InsertSlotAsync(world, await CreateRunAsync(world));
        var otherOrg = await CreateOtherOrgAsync(world, "other");

        (await CountAsync(world, otherOrg)).ShouldBe(0);
    }

    [Fact]
    public async Task Operator_WithTenantSet_ReadsZeroRows()
    {
        await using var world = await StartAsync();
        await InsertSlotAsync(world, await CreateRunAsync(world));

        // The operator holds the SELECT grant but no policy (TL T76, D1): with FORCE row level
        // security and `evidgate.org_id` set to the row's own org, the read is still zero.
        await using var scope = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(world.Database), world.Org);

        (await scope.ScalarAsync<Guid>("select evidgate.current_org_id()")).ShouldBe(world.Org);
        (await scope.ScalarAsync<long>("select count(*) from public.producer_slot")).ShouldBe(0);
    }

    [Fact]
    public async Task AppRole_HasNoDelete_AndOnlyTheGrantedUpdateColumns()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "producer_slot", "delete")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "producer_slot", "insert")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "producer_slot", "select")).ShouldBeTrue();

        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'bound_token_jti', 'update')")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'bound_ci_run_ref', 'update')")).ShouldBeTrue();
        // `last_sequence` was granted by 0034 (04.1.1-T1): the ingest handler's slot write names exactly
        // that column, and 0029's header handed the grant to this task. No grant on `version`/`updated_at`:
        // the shared trigger assigns them without a column privilege.
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'last_sequence', 'update')")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'version', 'update')")).ShouldBeFalse();
        // `closed_at` and the two declared values were granted by 0035 (04.1.2-T5): `CloseSlotAsync`'s
        // single conditional UPDATE names exactly those three columns (0029's header handed the grant to
        // this task). Every other column stays outside the app role's grants.
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'closed_at', 'update')")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'declared_last_sequence', 'update')")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'declared_count', 'update')")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'producer_id', 'update')")).ShouldBeFalse();
    }

    // ------------------------------------------------------------------ (c) constraints

    [Fact]
    public async Task SecondRow_SameRunProducer_Fails()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InsertSlotAsync(world, runId);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(InsertSlotSql(world.Org, runId, shardIndex: 2, producerId: "shard-1"));

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("producer_slot_run_producer_key");
    }

    [Fact]
    public async Task SecondRow_SameRunShard_Fails()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InsertSlotAsync(world, runId);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(InsertSlotSql(world.Org, runId, shardIndex: 1, producerId: "shard-9"));

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("producer_slot_run_shard_key");
    }

    [Fact]
    public async Task SameProducer_OtherOrg_Succeeds()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InsertSlotAsync(world, runId);
        var otherOrg = await CreateOtherOrgAsync(world, "other");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg);
        (await scope.ExecuteAsync(InsertSlotSql(otherOrg, await CreateRunAsync(world, otherOrg)))).ShouldBe(1);
        await scope.CommitAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public async Task ShardIndex_OutOfRange_FailsCheck(int shardIndex)
    {
        await using var world = await StartAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(InsertSlotSql(world.Org, await CreateRunAsync(world), shardIndex: shardIndex));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("producer_slot_shard_index_check");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("shard/1")]
    public async Task ProducerId_OutsideTheCharset_FailsCheck(string producerId)
    {
        await using var world = await StartAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(InsertSlotSql(world.Org, await CreateRunAsync(world), producerId: producerId));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("producer_slot_producer_id_check");
    }

    [Fact]
    public async Task BoundTokenJti_TooLong_FailsCheck()
    {
        await using var world = await StartAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(
            InsertSlotSql(world.Org, await CreateRunAsync(world), boundTokenJti: new string('a', 257)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("producer_slot_bound_token_jti_check");
    }

    [Fact]
    public async Task NegativeLastSequence_FailsCheck()
    {
        await using var world = await StartAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(InsertSlotSql(world.Org, await CreateRunAsync(world), lastSequence: -1));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("producer_slot_last_sequence_check");
    }

    // ------------------------------------------------------------------ (d) AC2: registration

    [Fact]
    public async Task RegisterSlots_PerShard_SameTx()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);

        // The slots are written inside the caller's transaction (T13): a rollback takes them away, so
        // the run row and its slots commit or not at all.
        await using (var services = world.Api.Services.CreateAsyncScope())
        {
            await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
                .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
            var registry = services.ServiceProvider.GetRequiredService<IProducerRegistry>();

            var result = await registry.RegisterSlotsAsync(runId, 3, TestContext.Current.CancellationToken);

            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            result.Value.Count.ShouldBe(3);
            result.Value.Select(slot => slot.ShardIndex).ShouldBe([1, 2, 3]);
            result.Value.Select(slot => slot.ProducerId).ShouldBe(["shard-1", "shard-2", "shard-3"]);

            // A separate connection cannot see the uncommitted rows, and the rollback leaves none.
            (await CountAsync(world, world.Org)).ShouldBe(0);
        }

        (await CountAsync(world, world.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisterSlots_CommitsWithTheCaller()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);

        var result = await InScopeAsync(
            world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await CountAsync(world, world.Org)).ShouldBe(2);
    }

    [Fact]
    public async Task RegisterSlots_SameCount_IsIdempotent()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);

        var first = await InScopeAsync(
            world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));
        var second = await InScopeAsync(
            world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        second.Value.Select(slot => slot.Id).ShouldBe(first.Value.Select(slot => slot.Id));
        (await CountAsync(world, world.Org)).ShouldBe(2);
    }

    [Fact]
    public async Task RegisterSlots_DifferentCount_Conflicts()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);

        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));
        var second = await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 3, TestContext.Current.CancellationToken));

        second.IsSuccess.ShouldBeFalse();
        second.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotsConflict);
        second.Error.Status.ShouldBe(409);
        (await CountAsync(world, world.Org)).ShouldBe(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(257)]
    public async Task RegisterSlots_OutOfRange_RejectedBeforeSql(int shardCount)
    {
        await using var world = await StartAsync();

        var result = await InScopeAsync(
            world, world.Org, registry => registry.RegisterSlotsAsync(Guid.CreateVersion7(), shardCount, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Status.ShouldBe(422);
        (await CountAsync(world, world.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisterSlots_OutsideTenantScope_FailsLoudly()
    {
        await using var world = await StartAsync();
        await using var services = world.Api.Services.CreateAsyncScope();

        await Should.ThrowAsync<TenantScopeMissingException>(async () =>
            await services.ServiceProvider.GetRequiredService<IProducerRegistry>()
                .RegisterSlotsAsync(Guid.CreateVersion7(), 1, TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------ (e) binding (T9)

    [Fact]
    public async Task BindOrCheck_UnknownProducer_422()
    {
        await using var world = await StartAsync();

        var result = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(Guid.CreateVersion7(), "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.UnknownProducer);
        result.Error.Status.ShouldBe(422);
    }

    [Fact]
    public async Task BindOrCheck_FirstBind_SetsJti_ThenSameIdentityIsIdempotent()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        var first = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));
        var second = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value.Bound.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        second.Value.Bound.ShouldBeFalse();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<string>("select bound_token_jti from public.producer_slot")).ShouldBe("jti-a");
        (await scope.ScalarAsync<string>("select bound_ci_run_ref from public.producer_slot")).ShouldBe("1/7/2");
    }

    [Fact]
    public async Task BindOrCheck_ForeignJti_403()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        var foreign = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-b", null, TestContext.Current.CancellationToken));

        foreign.IsSuccess.ShouldBeFalse();
        foreign.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        foreign.Error.Status.ShouldBe(403);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<string>("select bound_token_jti from public.producer_slot")).ShouldBe("jti-a");
    }

    [Fact]
    public async Task binds_then_foreign_identity_403()
    {
        // The plan's AC3 row: the FIRST write binds the slot to the caller's jti, and a foreign jti is then
        // refused with 403 producer_slot_taken - the same answer a foreign CI run gets (T88), so neither
        // learns more than that code about the slot (T9).
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        var bound = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));
        var foreign = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-b", null, TestContext.Current.CancellationToken));

        bound.IsSuccess.ShouldBeTrue(bound.Error?.Message);
        bound.Value.Bound.ShouldBeTrue();
        foreign.IsSuccess.ShouldBeFalse();
        foreign.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        foreign.Error.Status.ShouldBe(403);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<string>("select bound_token_jti from public.producer_slot")).ShouldBe("jti-a");
    }

    [Fact]
    public async Task BindOrCheck_CiRunRefMismatch_403()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));

        var mismatch = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", "1/7/3", TestContext.Current.CancellationToken));

        mismatch.IsSuccess.ShouldBeFalse();
        mismatch.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        mismatch.Error.Status.ShouldBe(403);
    }

    [Fact]
    public async Task BindOrCheck_ConcurrentFirstBinders_ExactlyOneWins()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        // Two first writers race for the same unbound slot: the conditional UPDATE is the arbiter, so
        // exactly one binds and the other is told the slot is taken (T9).
        var first = InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));
        var second = InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-b", null, TestContext.Current.CancellationToken));

        var results = await Task.WhenAll(first, second);

        results.Count(result => result.IsSuccess && result.Value.Bound).ShouldBe(1);
        results.Count(result => !result.IsSuccess && result.Error!.Code == ApiErrorCode.ProducerSlotTaken).ShouldBe(1);
    }

    [Fact]
    public async Task GetRunProducerSlots_ReturnsTheRunsSlots()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));
        await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-2", "jti-a", null, TestContext.Current.CancellationToken));

        var slots = await InScopeAsync(
            world, world.Org, registry => registry.GetRunProducerSlotsAsync(runId, TestContext.Current.CancellationToken));

        slots.Count.ShouldBe(2);
        slots[0].ShardIndex.ShouldBe(1);
        slots[0].Bound.ShouldBeFalse();
        slots[0].ClosedAt.ShouldBeNull();
        slots[0].LastSequence.ShouldBe(0);
        slots[1].ShardIndex.ShouldBe(2);
        slots[1].Bound.ShouldBeTrue();
    }

    [Fact]
    public async Task GetRunProducerSlots_OtherOrg_ReturnsEmpty()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        var otherOrg = await CreateOtherOrgAsync(world, "other");

        var slots = await InScopeAsync(
            world, otherOrg, registry => registry.GetRunProducerSlotsAsync(runId, TestContext.Current.CancellationToken));

        slots.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ (f) the EF write model (T4.1)

    [Fact]
    public async Task WriteModel_RoundTripsThroughEfAndRawSql()
    {
        // T4.1: the entity and its configuration are the module's mapping of migration 0029, so a row
        // written through the EF context must read back identically through EF and through raw SQL -
        // the version default, the trigger-owned timestamps and the nullable binding columns included.
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        var id = Guid.CreateVersion7();

        await using (var services = world.Api.Services.CreateAsyncScope())
        {
            await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
                .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
            await using var context = services.ServiceProvider
                .GetRequiredService<ITenantDbContextFactory<IngestionDbContext>>().Create();

            await context.ProducerSlots.AddAsync(new ProducerSlot(id, world.Org, runId, 1, "shard-1"), TestContext.Current.CancellationToken);
            (await context.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

            var read = await context.ProducerSlots.SingleAsync(row => row.Id == id, TestContext.Current.CancellationToken);
            read.ShardIndex.ShouldBe(1);
            read.ProducerId.ShouldBe("shard-1");
            read.Version.ShouldBe(1);
            read.LastSequence.ShouldBe(0);
            read.BoundTokenJti.ShouldBeNull();
            read.BoundCiRunRef.ShouldBeNull();
            read.ClosedAt.ShouldBeNull();
            read.CreatedAt.ShouldNotBe(default);

            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        await using var raw = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await raw.ScalarAsync<long>("select count(*) from public.producer_slot")).ShouldBe(1);
        (await raw.ScalarAsync<int>("select version from public.producer_slot")).ShouldBe(1);
        (await raw.ScalarAsync<string>("select producer_id from public.producer_slot")).ShouldBe("shard-1");
        (await raw.ScalarAsync<long>("select last_sequence from public.producer_slot")).ShouldBe(0);
    }
}
