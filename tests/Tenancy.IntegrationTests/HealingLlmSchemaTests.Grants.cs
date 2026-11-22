using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class HealingLlmSchemaTests
{
    // ------------------------------------------------------------------ (e) the grants

    [Fact]
    public async Task AppRole_CanInsertAndReadCalls_ButNotRewriteOrRemoveThem()
    {
        var world = await SeedTenantsAsync();

        // Column-level INSERT and nothing table-level (0052 precedent): a table-level grant would let a
        // caller name `created_at` and backdate an attempt.
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", CallTable, "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", CallTable, "insert"))
            .ShouldBeFalse("INSERT is granted per column, never on the table");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", CallTable, "update"))
            .ShouldBeFalse("0054 grants no UPDATE: the ledger is append-only");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", CallTable, "delete"))
            .ShouldBeFalse("0054 grants no DELETE: the ledger is append-only");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_operator", CallTable, "select"))
            .ShouldBeFalse("0055 revokes the operator's SELECT: the tenant policy names evidgate_app and no read path uses the operator");

        foreach (var column in new[]
        {
            "id", "org_id", "project_id", "incident_id", "candidate_id", "purpose", "model",
            "prompt_version", "tokens", "cost", "status", "attempt", "redaction_report_hash",
        })
        {
            (await _fixture.ScalarAsync<bool>(
                    world.Database,
                    $"select has_column_privilege('evidgate_app', 'public.{CallTable}', '{column}', 'INSERT')"))
                .ShouldBeTrue($"the app role inserts `{column}`");
        }

        (await _fixture.ScalarAsync<bool>(
                world.Database,
                $"select has_column_privilege('evidgate_app', 'public.{CallTable}', 'created_at', 'INSERT')"))
            .ShouldBeFalse("`created_at` belongs to the column default, not to the caller");

        // The 00 §7 convention columns exist for the shape and are outside every app grant: no INSERT, so
        // a caller cannot set the token, and no UPDATE (the table-level refusal above is the whole story,
        // this pins the column-level one too).
        foreach (var column in new[] { "updated_at", "version" })
        {
            (await _fixture.ScalarAsync<bool>(
                    world.Database,
                    $"select has_column_privilege('evidgate_app', 'public.{CallTable}', '{column}', 'INSERT')"))
                .ShouldBeFalse($"`{column}` is outside the app-role INSERT grant (00 §7 convention column)");
            (await _fixture.ScalarAsync<bool>(
                    world.Database,
                    $"select has_column_privilege('evidgate_app', 'public.{CallTable}', '{column}', 'UPDATE')"))
                .ShouldBeFalse($"`{column}` is outside every app-role grant: the ledger is append-only");
        }

        // The recorder's write: one attempt row inside the caller's tenant transaction (T1.2 seam).
        await using (var recorder = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await recorder.ExecuteAsync(
                    InsertCallSql,
                    CallParameters(
                        Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                        Guid.CreateVersion7(), attempt: 2)))
                .ShouldBe(1, "select + a column-level insert is the recorder's whole grant");
            await recorder.CommitAsync();
        }

        // One scope per refused statement: an ACL error aborts its transaction, so the second refusal
        // needs its own transaction to be the ACL's answer and not the aborted state's.
        await using (var rewriting = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await rewriting.CaptureErrorAsync(
                    $"update public.{CallTable} set status = 'error'"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the app role cannot rewrite an attempt");
        }

        await using (var removing = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await removing.CaptureErrorAsync($"delete from public.{CallTable}"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the app role cannot remove an attempt");
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{CallTable} where org_id = '{world.OrgA}'"))
            .ShouldBe(1, "the refused statements left the ledger alone");

        // The convention token is inert on this table: the recorder's insert left it at the default and
        // nothing the app role can issue moves it (there is no UPDATE grant at all).
        (await _fixture.ScalarAsync<int>(world.Database, $"select version from public.{CallTable} where org_id = '{world.OrgA}'"))
            .ShouldBe(1, "the 00 §7 token stays at the column default 0054 gives it");
    }

    [Fact]
    public async Task AppRole_ConsumesTheBudgetRow_ButCannotReTargetIt()
    {
        var world = await SeedTenantsAsync();

        // Provisioning is the owner path's: the caps are NOT in the app role's INSERT grant, and a row
        // that could name them would let a tenant raise its own ceiling (owed to 08.5.1-T1.4/T1.7).
        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await seed.ExecuteAsync(
                    InsertCapacitySql,
                    CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA)))
                .ShouldBe(1, "the owner provisions the cap row the consume runs against");
            await seed.CommitAsync();
        }

        // AC3's consume: the update the hard stop runs, admitted on exactly the columns it moves.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ExecuteAsync(
                    $"""
                    update public.{BudgetTable}
                       set used_cost = used_cost + 0.000250, used_tokens = used_tokens + 12
                     where org_id = @org_id
                       and project_id = @project_id
                       and period = 'daily'
                       and used_cost + 0.000250 <= period_cap_cost
                    """,
                    ("org_id", world.OrgA),
                    ("project_id", world.ProjectA)))
                .ShouldBe(1, "the consumption columns are the app role's whole UPDATE grant");
            await scope.CommitAsync();
        }

        // `version` moved to 2 with no column privilege on it: the 0004 trigger assigns `updated_at` and
        // `version` to NEW, and PostgreSQL checks the columns a statement names, not a trigger's writes.
        (await _fixture.ScalarAsync<string>(
                world.Database,
                $"select used_cost::text || ':' || used_tokens || ':' || version from public.{BudgetTable} "
                + $"where org_id = '{world.OrgA}' and project_id = '{world.ProjectA}'"))
            .ShouldBe("0.000250:12:2", "the trigger moved version on the consume, as 0004 does");

        // Everything else is outside the grant: the caps (raising one is exactly the ceiling escape this
        // test refuses), the trigger-owned token, the identity, the period and the creation stamp. One
        // scope per statement, because an ACL error aborts the transaction that caused it.
        foreach (var set in new[]
        {
            "period_cap_cost = period_cap_cost + 1",
            "period_cap_tokens = period_cap_tokens + 1",
            "updated_at = now()",
            "version = 1",
            "id = gen_random_uuid()",
            $"org_id = '{Guid.CreateVersion7()}'",
            "period = 'weekly'",
            "period_start = current_date + 1",
            "created_at = now()",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA);
            (await scope.CaptureErrorAsync($"update public.{BudgetTable} set {set}"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, set);
        }

        await using (var removing = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await removing.CaptureErrorAsync($"delete from public.{BudgetTable}"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the app role cannot remove a counter row");
        }
    }

    [Fact]
    public async Task AppRole_CannotProvisionOrRaiseTheBudgetRow()
    {
        var world = await SeedTenantsAsync();

        // The grant matrix, column by column: the app role may move the two usage counters and name the
        // five identity/period columns on INSERT, and nothing else.
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", BudgetTable, "select"))
            .ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", BudgetTable, "insert"))
            .ShouldBeFalse("INSERT is granted per column, never on the table");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", BudgetTable, "update"))
            .ShouldBeFalse("UPDATE is granted per column, never on the table");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", BudgetTable, "delete"))
            .ShouldBeFalse("0054 grants no DELETE: the project cascade is the only delete path");

        foreach (var column in new[] { "id", "org_id", "project_id", "period", "period_start" })
        {
            (await ColumnPrivilegeAsync(world.Database, BudgetTable, column, "INSERT"))
                .ShouldBeTrue($"the app role names `{column}` on INSERT");
        }

        foreach (var column in new[]
        {
            "period_cap_cost", "period_cap_tokens", "used_cost", "used_tokens", "created_at",
            "updated_at", "version",
        })
        {
            (await ColumnPrivilegeAsync(world.Database, BudgetTable, column, "INSERT"))
                .ShouldBeFalse($"`{column}` is not the caller's to name on INSERT");
        }

        foreach (var column in new[] { "used_cost", "used_tokens" })
        {
            (await ColumnPrivilegeAsync(world.Database, BudgetTable, column, "UPDATE"))
                .ShouldBeTrue($"the consume moves `{column}`");
        }

        foreach (var column in new[]
        {
            "period_cap_cost", "period_cap_tokens", "updated_at", "version",
            "id", "org_id", "project_id", "period", "period_start", "created_at",
        })
        {
            (await ColumnPrivilegeAsync(world.Database, BudgetTable, column, "UPDATE"))
                .ShouldBeFalse($"`{column}` is outside the app role's UPDATE grant");
        }

        // The cap INSERT the tenants' own path would need is refused at the ACL, so no tenant transaction
        // can even reach the NOT NULL check with a cap it chose.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCapacitySql,
                CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA));

            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the caps are not the app role's to name");
        }

        // And the residual INSERT grant cannot complete a row either: with the caps outside it, the NOT
        // NULL cap column refuses a statement that names only what the app role may name.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                """
                insert into public.healing_llm_budget
                  (id, org_id, project_id, period, period_start)
                values (@id, @org_id, @project_id, @period, @period_start)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.OrgA),
                ("project_id", world.ProjectA),
                ("period", "daily"),
                ("period_start", PeriodStart));

            error.SqlState.ShouldBe(PostgresErrorCodes.NotNullViolation, "no app-role statement can provision a budget row");
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{BudgetTable} where org_id = '{world.OrgA}'"))
            .ShouldBe(0, "the refused statements left no counter row behind");
    }

    [Fact]
    public async Task Tenants_SeeTheirOwnCallsAndBudget_AndNothingElse()
    {
        var world = await SeedTenantsAsync();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ExecuteAsync(
                    InsertCallSql,
                    CallParameters(
                        Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                        Guid.CreateVersion7()))).ShouldBe(1);
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            (await scope.ExecuteAsync(
                    InsertCallSql,
                    CallParameters(
                        Guid.CreateVersion7(), world.OrgB, world.ProjectB, "candidate_generation",
                        Guid.CreateVersion7()))).ShouldBe(1);
            await scope.CommitAsync();
        }

        // One cap row per project, provisioned through the owner path: the app role cannot name a cap, so
        // the tenant's own scope only ever reads the row and moves its usage.
        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await seed.ExecuteAsync(
                    InsertCapacitySql,
                    CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA)))
                .ShouldBe(1);
            (await seed.ExecuteAsync(
                    InsertCapacitySql,
                    CapacityParameters(Guid.CreateVersion7(), world.OrgB, world.ProjectB)))
                .ShouldBe(1);
            await seed.CommitAsync();
        }

        // Org A sees one call and one counter, both its own; org B's rows are invisible — and an UPDATE
        // aimed at them matches nothing rather than reaching across the boundary.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgA))
        {
            (await scope.ScalarAsync<long>($"select count(*) from public.{CallTable}")).ShouldBe(1);
            (await scope.ScalarAsync<long>(
                    $"select count(*) from public.{CallTable} where org_id = @org_id",
                    ("org_id", world.OrgA))).ShouldBe(1, "the visible row is this org's own");
            (await scope.ScalarAsync<long>($"select count(*) from public.{BudgetTable}")).ShouldBe(1);
            (await scope.ExecuteAsync(
                    $"update public.{BudgetTable} set used_tokens = used_tokens + 1"))
                .ShouldBe(1, "only this org's counter row is reachable by the consume");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            (await scope.ScalarAsync<long>(
                    $"select count(*) from public.{CallTable} where org_id = @org_id",
                    ("org_id", world.OrgB))).ShouldBe(1);
            (await scope.ScalarAsync<long>(
                    $"select count(*) from public.{BudgetTable} where org_id = @org_id",
                    ("org_id", world.OrgB))).ShouldBe(1);
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.{CallTable} where org_id in ('{world.OrgA}', '{world.OrgB}')"))
            .ShouldBe(2, "both tenants' rows are there; only the policy hides them");
    }
}
