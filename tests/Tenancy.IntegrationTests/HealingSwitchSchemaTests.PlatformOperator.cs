using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The platform-scope half of <see cref="HealingSwitchSchemaTests"/>: the trail
/// <c>public.platform_operator_audit</c> that migration 0105 appends one row to per accepted call of
/// <c>evidgate.healing_switch_set_system</c>.
/// <para>
/// It lives in its own part because the switch's shape, tenant-boundary and resolution tests already fill
/// the main file: what is asserted here is that the row is written exactly once per call with the actor and
/// the mode the call replaced, that a refused call writes none, and that no runtime role can write or read
/// the trail directly (one guard trigger, FORCE row level security, owner insert/select policies only).
/// </para>
/// </summary>
public sealed partial class HealingSwitchSchemaTests
{
    [Fact]
    public async Task OperatorCall_AppendsExactlyOneTrailRowPerSwitchChange()
    {
        var world = await SeedTenantsAsync();
        var creatingActor = Guid.CreateVersion7();
        var replacingActor = Guid.CreateVersion7();

        await SetSystemAsync(world, "disabled", creatingActor);
        await SetSystemAsync(world, "shadow", replacingActor);

        // One row per call, in call order: the create branch names no old mode, the update names the
        // mode it replaced, and `version` is the version the call produced.
        (await AuditRowsAsync(world.Database)).ShouldBe(
            [
                $"{creatingActor}:healing.switch.system_set:-:disabled:1",
                $"{replacingActor}:healing.switch.system_set:disabled:shadow:2",
            ]);

        // The trail is platform scope: no org, and no reason text (which may name a customer).
        (await _fixture.ScalarAsync<string>(
            world.Database,
            """
            select string_agg(column_name, ',' order by ordinal_position)
              from information_schema.columns
             where table_schema = 'public' and table_name = 'platform_operator_audit'
            """))
            .ShouldBe("id,actor,action,old_mode,new_mode,version,created_at");

        // A refused call appends none: the switch write and its row share one transaction.
        await using (var platformOperator = await TenantScope.BeginAsync(_fixture.PlatformOperatorConnectionString(world.Database)))
        {
            (await platformOperator.CaptureErrorAsync(
                "select * from evidgate.healing_switch_set_system('unavailable', null, @set_by)",
                ("set_by", Guid.CreateVersion7())))
                .SqlState.ShouldBe(PostgresErrorCodes.InvalidParameterValue);
        }

        (await AuditCountAsync(world.Database)).ShouldBe(2, "a refused call appends no trail row");
    }

    [Fact]
    public async Task PlatformOperatorAudit_IsAppendOnlyAndUnreadableByRuntimeRoles()
    {
        var world = await SeedTenantsAsync();
        await SetSystemAsync(world, "disabled");

        var (rlsEnabled, rlsForced) = await _fixture.RowSecurityAsync(world.Database, "platform_operator_audit");
        rlsEnabled.ShouldBeTrue("the trail has row level security");
        rlsForced.ShouldBeTrue("FORCE applies it to the owner too, the DECISIONS F5 posture");

        // No runtime role holds a table privilege there: the row is written by the definer, which runs
        // as the owner, and a direct write would be an unaudited way to forge the trail.
        foreach (var role in new[] { "evidgate_app", "evidgate_worker", "evidgate_worker_sched", "evidgate_platform_operator" })
        {
            foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
            {
                (await _fixture.HasTablePrivilegeAsync(world.Database, role, "platform_operator_audit", privilege))
                    .ShouldBeFalse($"{role} must hold no {privilege} on the trail");
            }
        }

        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_platform_operator", "healing_switch", "UPDATE"))
            .ShouldBeFalse("the switch is written through the definer, never directly");

        // The app role is refused by the grant (42501), one statement per transaction: a refusal aborts it.
        (await CaptureAppErrorAsync(world.Database, world.OrgA, "select count(*) from public.platform_operator_audit"))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await CaptureAppErrorAsync(world.Database, world.OrgA, "update public.platform_operator_audit set new_mode = 'enabled'"))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await CaptureAppErrorAsync(world.Database, world.OrgA, "delete from public.platform_operator_audit"))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // The operator holds the SELECT grant and no policy, so FORCE row level security yields zero
        // rows until 02.8.1's operator scope opens the grant (0052's posture).
        await using (var operatorScope = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(world.Database)))
        {
            (await operatorScope.ScalarAsync<long>("select count(*) from public.platform_operator_audit"))
                .ShouldBe(0, "the operator's read is grant-gated, not policy-scoped");
        }

        // Append-only at the second layer. The owner holds insert and select policies only, so FORCE
        // row level security leaves its UPDATE and DELETE zero rows and the guard trigger never fires
        // for them; the superuser bypasses RLS, so there the trigger is what refuses.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync("update public.platform_operator_audit set new_mode = 'enabled'"))
                .ShouldBe(0, "no UPDATE policy admits the owner");
            (await owner.ExecuteAsync("delete from public.platform_operator_audit"))
                .ShouldBe(0, "no DELETE policy admits the owner");
        }

        (await CaptureSuperuserErrorAsync(
            world.Database,
            "update public.platform_operator_audit set new_mode = 'enabled'"))
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await CaptureSuperuserErrorAsync(world.Database, "delete from public.platform_operator_audit"))
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);

        var rows = await AuditRowsAsync(world.Database);
        rows.Count.ShouldBe(1, "the refused statements left the row intact");
        rows[0].ShouldContain(":disabled:1");
    }

    /// <summary>
    /// One statement as the app role in a tenant transaction of its own, returning the server error: a
    /// refusal aborts its transaction, so every attempt needs a fresh scope.
    /// </summary>
    private async Task<PostgresException> CaptureAppErrorAsync(
        string database,
        Guid org,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
        return await scope.CaptureErrorAsync(sql, parameters);
    }

    /// <summary>One statement as the superuser, which row level security does not filter.</summary>
    private async Task<PostgresException> CaptureSuperuserErrorAsync(string database, string sql)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await Should.ThrowAsync<PostgresException>(
            async () => await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }
}
