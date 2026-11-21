using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class UsageLedgerSchemaTests
{
    // ------------------------------------------------------------------ (g) T1.2: the roll-up

    [Fact]
    public async Task Rollup_Uniqueness_CountsANullProjectAsOneScope()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        var firstId = Guid.CreateVersion7();
        var first = RollupInsert(firstId, org, projectId: null, totalQuantity: 10m, rowCount: 1);
        await ExecuteAsync(_fixture.AppConnectionString(database), org, first);

        // Two org-level rows (project NULL) for one period+metric are the same scope: a plain
        // four-column unique would accept both, which is exactly why the index coalesces the project.
        // The refused statement aborts its transaction (25P02), so each attempt gets its own scope.
        var duplicate = RollupInsert(Guid.CreateVersion7(), org, projectId: null, totalQuantity: 12m, rowCount: 2);
        var nullProjectError = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, duplicate.Sql, duplicate.Parameters);
        nullProjectError.SqlState.ShouldBe("23505");
        nullProjectError.ConstraintName.ShouldBe("usage_rollup_scope_key");

        // The other unique of the pair (T1.2): a second aggregate may not reuse the row id.
        var reusedId = RollupInsert(firstId, org, projectId: null, totalQuantity: 3m, rowCount: 1, period: "2026-12");
        var reusedIdError = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, reusedId.Sql, reusedId.Parameters);
        reusedIdError.SqlState.ShouldBe("23505");
        reusedIdError.ConstraintName.ShouldBeOneOf(["usage_rollup_pkey", "usage_rollup_org_id_key"], "a reused row id breaks a tenant key");

        // The same period and metric for a real project is a different scope and is accepted.
        var scoped = RollupInsert(Guid.CreateVersion7(), org, project, totalQuantity: 4m, rowCount: 1);
        await ExecuteAsync(_fixture.AppConnectionString(database), org, scoped);

        var scopedDuplicate = RollupInsert(Guid.CreateVersion7(), org, project, totalQuantity: 5m, rowCount: 2);
        var scopedError = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, scopedDuplicate.Sql, scopedDuplicate.Parameters);
        scopedError.SqlState.ShouldBe("23505");
        scopedError.ConstraintName.ShouldBe("usage_rollup_scope_key");

        // A different period is a different scope as well.
        var nextPeriod = RollupInsert(Guid.CreateVersion7(), org, project, totalQuantity: 1m, rowCount: 1, period: "2026-11");
        await ExecuteAsync(_fixture.AppConnectionString(database), org, nextPeriod);

        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.usage_rollup where org_id = '{org}'")).ShouldBe(3);
    }

    [Fact]
    public async Task Rollup_Recompute_UpsertsOnTheCoalesceIndex()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        var first = RollupInsert(Guid.CreateVersion7(), org, projectId: null, totalQuantity: 10m, rowCount: 1);
        (await app.ExecuteAsync(first.Sql, first.Parameters)).ShouldBe(1);

        // The recompute's second run: the same arbiter expression the unique index is built on, so the
        // org-level aggregate is replaced in place instead of appended (T1.2 done-when, AC6).
        var second = RollupInsert(Guid.CreateVersion7(), org, projectId: null, totalQuantity: 25m, rowCount: 3);
        var upserted = await app.ExecuteAsync(
            second.Sql
            + $" on conflict (org_id, coalesce(project_id, '{NullProjectSentinel}'::uuid), period, metric) "
            + "do update set total_quantity = excluded.total_quantity, "
            + "row_count = excluded.row_count, computed_at = excluded.computed_at",
            second.Parameters);

        upserted.ShouldBe(1, "the recompute must hit the coalesce index instead of inserting a second aggregate");

        (await app.ScalarAsync<long>($"select count(*) from public.usage_rollup where org_id = '{org}'")).ShouldBe(1);
        (await app.ScalarAsync<long>(
            $"select count(*) from public.usage_rollup where org_id = '{org}' "
            + "and project_id is null and period = '2026-10' and metric = 'results' "
            + "and total_quantity = 25 and row_count = 3 and version = 2"))
            .ShouldBe(1, "an upserted aggregate keeps one row, takes the new totals and bumps version");

        // The project-scoped aggregate upserts on the same expression with a real project id.
        var scoped = RollupInsert(Guid.CreateVersion7(), org, project, totalQuantity: 3m, rowCount: 1);
        (await app.ExecuteAsync(scoped.Sql, scoped.Parameters)).ShouldBe(1);

        var scopedAgain = RollupInsert(Guid.CreateVersion7(), org, project, totalQuantity: 8m, rowCount: 4);
        (await app.ExecuteAsync(
            scopedAgain.Sql
            + $" on conflict (org_id, coalesce(project_id, '{NullProjectSentinel}'::uuid), period, metric) "
            + "do update set total_quantity = excluded.total_quantity, row_count = excluded.row_count",
            scopedAgain.Parameters)).ShouldBe(1);

        (await app.ScalarAsync<decimal>(
            $"select total_quantity from public.usage_rollup where org_id = '{org}' and project_id = '{project}'"))
            .ShouldBe(8m);

        // The app role may recompute but not delete an aggregate, and not rewrite its key. UPDATE is
        // table-level FALSE because 0047 grants it per column (0028/0033 precedent): a table-wide check
        // says nothing about a column grant, so both halves are asserted.
        foreach (var (privilege, expected) in new[]
        {
            ("SELECT", true),
            ("INSERT", true),
            ("UPDATE", false),
            ("DELETE", false),
            ("TRUNCATE", false),
        })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "usage_rollup", privilege))
                .ShouldBe(expected, $"evidgate_app {privilege} on usage_rollup");
        }

        foreach (var column in new[] { "total_quantity", "total_amount", "currency", "row_count", "gap_flag", "computed_at" })
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_column_privilege('evidgate_app', 'public.usage_rollup', '{column}', 'UPDATE')"))
                .ShouldBeTrue($"evidgate_app recomputes {column}");
        }

        foreach (var column in new[] { "id", "org_id", "project_id", "period", "metric" })
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_column_privilege('evidgate_app', 'public.usage_rollup', '{column}', 'UPDATE')"))
                .ShouldBeFalse($"a recompute must never move an aggregate to another {column} (0047 header)");
        }
    }

    [Fact]
    public async Task Rollup_AppRole_CannotDeleteTruncateOrRewriteTheKey()
    {
        // Dedicated: TRUNCATE asks for ACCESS EXCLUSIVE on tables every other test reads.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        var rollupId = Guid.CreateVersion7();
        await ExecuteAsync(
            _fixture.AppConnectionString(database),
            org,
            RollupInsert(rollupId, org, project, totalQuantity: 7m, rowCount: 2));

        // F-J: DELETE, TRUNCATE and every key column are refused at the ACL in execution, not only in
        // the catalogue. 0047 grants the recompute UPDATE per column and no DELETE/TRUNCATE at all, so
        // an aggregate can never be deleted, duplicated by a truncate or moved to another scope.
        foreach (var (label, sql) in new[]
        {
            ("delete the aggregate", $"delete from public.usage_rollup where id = '{rollupId}'"),
            ("move the aggregate to another period", $"update public.usage_rollup set period = '2026-11' where id = '{rollupId}'"),
            ("move the aggregate to org level", $"update public.usage_rollup set project_id = null where id = '{rollupId}'"),
            ("rewrite the aggregate's row id", $"update public.usage_rollup set id = '{Guid.CreateVersion7()}' where id = '{rollupId}'"),
            ("truncate the aggregates", "truncate public.usage_rollup"),
            ("truncate the ledger", "truncate public.usage_ledger_entry"),
        })
        {
            var error = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, sql);

            error.SqlState.ShouldBe("42501", $"{label} must be a permission error for evidgate_app");
        }

        // Every refused statement left the aggregate byte-identical for its own scope.
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_rollup where id = '{rollupId}' "
            + $"and org_id = '{org}' and project_id = '{project}' and period = '2026-10' "
            + "and total_quantity = 7 and row_count = 2 and version = 1"))
            .ShouldBe(1, "a refused DELETE/TRUNCATE/key UPDATE must leave the aggregate in place");
    }

    // ------------------------------------------------------------------ (h) T2/T3: RLS isolation

    [Fact]
    public async Task UsageTables_CrossTenantRead_ZeroRows()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var (otherOrg, _) = await SeedTenantAsync(database, "beta", "store");

        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var ledger = LedgerInsert(Guid.CreateVersion7(), org, project);
            (await app.ExecuteAsync(ledger.Sql, ledger.Parameters)).ShouldBe(1);
            var rollup = RollupInsert(Guid.CreateVersion7(), org, projectId: null, totalQuantity: 1m, rowCount: 1);
            (await app.ExecuteAsync(rollup.Sql, rollup.Parameters)).ShouldBe(1);
            await app.CommitAsync();
        }

        // Another tenant's scope sees neither table.
        await using (var other = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), otherOrg))
        {
            (await other.ScalarAsync<Guid>("select evidgate.current_org_id()")).ShouldBe(otherOrg);
            (await other.ScalarAsync<long>("select count(*) from public.usage_ledger_entry")).ShouldBe(0);
            (await other.ScalarAsync<long>("select count(*) from public.usage_rollup")).ShouldBe(0);

            // Writing into the other tenant's rows is refused by each policy's with-check half, on
            // both tables: the tenant id in the payload is what the policy compares against. Each
            // refused statement aborts its own transaction (25P02), so each gets its own scope.
            var foreign = LedgerInsert(Guid.CreateVersion7(), org, project);
            var denied = await other.CaptureErrorAsync(foreign.Sql, foreign.Parameters);
            denied.SqlState.ShouldBe("42501", "a cross-tenant ledger INSERT must fail the with-check");
        }

        await using (var other = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), otherOrg))
        {
            var foreignRollup = RollupInsert(
                Guid.CreateVersion7(),
                org,
                projectId: null,
                totalQuantity: 2m,
                rowCount: 1);
            var deniedRollup = await other.CaptureErrorAsync(foreignRollup.Sql, foreignRollup.Parameters);
            deniedRollup.SqlState.ShouldBe("42501", "a cross-tenant roll-up INSERT must fail the with-check");
        }

        // No tenant context: the helpers return NULL, so the policies match no row.
        await using (var unset = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            (await unset.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
            (await unset.ScalarAsync<long>("select count(*) from public.usage_ledger_entry")).ShouldBe(0);
            (await unset.ScalarAsync<long>("select count(*) from public.usage_rollup")).ShouldBe(0);
        }

        // The operator's SELECT grant reads zero rows without a policy (TL T76, D1).
        await using (var op = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(database), org))
        {
            (await op.ScalarAsync<long>("select count(*) from public.usage_ledger_entry")).ShouldBe(0);
            (await op.ScalarAsync<long>("select count(*) from public.usage_rollup")).ShouldBe(0);
        }

        // The row is still there for its own tenant, so the zeros above are the policy and not an
        // empty table.
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}'"))
            .ShouldBe(1);
    }
}
