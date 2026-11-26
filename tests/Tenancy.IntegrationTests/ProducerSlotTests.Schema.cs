using Evidgate.Modules.Ingestion.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ProducerSlotTests
{
    // ------------------------------------------------------------------ (a) migration: RLS flags

    [Fact]
    public async Task ProducerSlot_HasForcedRls()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.RowSecurityAsync(database, "producer_slot")).ShouldBe((true, true));
    }

    [Fact]
    public async Task TenantPolicies_AreScopedToTheAppRole()
    {
        var database = await _fixture.SharedDatabaseAsync();

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
        var database = await _fixture.SharedDatabaseAsync();

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
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.IndexesAsync(database, "producer_slot")).Any(index => index.StartsWith("producer_slot_run:", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeclaredColumns_ChecksAndUpdateGrants_Exist()
    {
        var database = await _fixture.SharedDatabaseAsync();

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

        // The UPDATE grants the app role holds on the table: the owner column's (`bound_token_jti`) and the
        // dead `bound_ci_run_ref`'s are the migration-0029 grants that still exist (the registry writes only
        // the former), plus the ingest handler's `last_sequence` (0034) and `CloseSlotAsync`'s three (0035).
        // Every other column stays outside the grant.
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
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "producer_slot", "delete")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "producer_slot", "insert")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "producer_slot", "select")).ShouldBeTrue();

        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.producer_slot', 'bound_token_jti', 'update')")).ShouldBeTrue();
        // Migration state, not production use: the unused column keeps its 0029 grant until a migration drops it.
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
    public async Task BoundOwnerId_TooLong_FailsCheck()
    {
        await using var world = await StartAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(
            InsertSlotSql(world.Org, await CreateRunAsync(world), boundOwnerId: new string('a', ProducerSlotLimits.MaxOwnerIdLength + 1)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("producer_slot_bound_token_jti_check");
    }

    [Fact]
    public async Task LegacyCiRunRef_TooLong_FailsMigrationCheck()
    {
        // The column is unused by the registry; its 0029 CHECK is asserted only while the column exists.
        await using var world = await StartAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(
            InsertSlotSql(world.Org, await CreateRunAsync(world), legacyCiRunRef: new string('a', 257)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("producer_slot_bound_ci_run_ref_check");
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
}
