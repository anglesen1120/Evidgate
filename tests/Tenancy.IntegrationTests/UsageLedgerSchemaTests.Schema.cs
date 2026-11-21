using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class UsageLedgerSchemaTests
{
    // ------------------------------------------------------------------ (a) migrations apply

    [Fact]
    public async Task Migrations_BothFilesJournalled_OnTheMigratedDatabase()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var table in UsageTables)
        {
            (await _fixture.TableExistsAsync(database, table)).ShouldBeTrue(table);
        }

        var journal = await _fixture.ReadJournalAsync(database);

        journal.ShouldContain(row => row.Name == LedgerMigrationFile, "0046 must be journalled by the real runner");
        journal.ShouldContain(row => row.Name == RollupMigrationFile, "0047 must be journalled by the real runner");
        journal.ShouldAllBe(row => row.Checksum.Length == 64);
    }

    [Fact]
    public async Task Migrations_ApplyForward_OnADatabaseAlreadyHoldingData()
    {
        // Dedicated: it applies migrations forward on an empty database and counts the ledger table whole.
        await using var database = await _fixture.CreateEmptyDedicatedDatabaseAsync();

        // Every file lexically before 0046: the real expand path, which keeps working as later
        // migrations are added to the repository (AuthoringSchemaTests precedent).
        var prefixes = Directory.EnumerateFiles(PostgresTenancyFixture.RepoMigrationsPath, "*.sql")
            .Select(Path.GetFileName)
            .Where(name => string.CompareOrdinal(name, LedgerMigrationFile) < 0)
            .Select(name => name![..4])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        using var history = TempMigrationDirectory.CreateFrom(PostgresTenancyFixture.RepoMigrationsPath, prefixes);
        var baselined = _fixture.ApplyMigrations(database, history.Path);

        baselined.ExitCode.ShouldBe(MigratorExitCodes.Success, baselined.Transcript);
        baselined.Result.AppliedNow.ShouldNotContain(LedgerMigrationFile);

        // Data that predates the usage tables: an org and a project, written as the migration role.
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        var forward = _fixture.ApplyMigrations(database);

        forward.ExitCode.ShouldBe(MigratorExitCodes.Success, forward.Transcript);
        forward.Result.AppliedNow.ShouldContain(LedgerMigrationFile);
        forward.Result.AppliedNow.ShouldContain(RollupMigrationFile);
        forward.Result.AppliedNow
            .Where(name => string.CompareOrdinal(name, LedgerMigrationFile) < 0)
            .ShouldBeEmpty($"no file before 0046 may be re-applied; applied: {string.Join(", ", forward.Result.AppliedNow)}");
        forward.Result.AppliedNow.Order(StringComparer.Ordinal).ToArray()
            .ShouldBe(forward.Result.AppliedNow.ToArray(), "the pending files are applied in lexical order");

        // The pre-existing tenant is untouched, and the new ledger accepts a row that references it —
        // the FK from a fresh table onto data written before the table existed.
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.organization where id = '{org}'"))
            .ShouldBe(1, "the forward apply must not touch existing rows");
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.project where id = '{project}'"))
            .ShouldBe(1);

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), org))
        {
            var row = LedgerInsert(Guid.CreateVersion7(), org, project);
            (await owner.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await owner.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.usage_ledger_entry")).ShouldBe(1);

        // The roles the new grants name exist and hold what 0046/0047 give them (a login is enabled by
        // the fixture's create path only, so the catalogue is the readable proof on this path).
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "usage_ledger_entry", "INSERT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "usage_rollup", "INSERT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "usage_rollup", "UPDATE"))
            .ShouldBeFalse("0047 grants UPDATE per column, never table-wide");
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.usage_rollup', 'total_quantity', 'UPDATE')"))
            .ShouldBeTrue("the recompute updates the derived total");
    }

    // ------------------------------------------------------------------ (b) shape, keys, RLS flags

    [Fact]
    public async Task UsageTables_DeclareThePlanColumnAndKeyContract()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var ledgerColumns = await _fixture.ColumnsAsync(database, "usage_ledger_entry");
        foreach (var prefix in new[]
        {
            "id:uuid:uuid:NO:",
            "org_id:uuid:uuid:NO:",
            "project_id:uuid:uuid:YES:",
            "metric:text:text:NO:",
            "quantity:numeric:numeric:NO:",
            "unit:text:text:NO:",
            "period:text:text:NO:",
            "billing_key:text:text:NO:",
            "occurred_at:timestamp with time zone:timestamptz:NO:",
            "price_ref:uuid:uuid:YES:",
            "amount:numeric:numeric:YES:",
            "currency:text:text:YES:",
            "adjustment_of:uuid:uuid:YES:",
            "reason:text:text:YES:",
            "payload_hash:text:text:YES:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:",
        })
        {
            ledgerColumns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"usage_ledger_entry needs the plan §4 column shape '{prefix}'");
        }

        var rollupColumns = await _fixture.ColumnsAsync(database, "usage_rollup");
        foreach (var prefix in new[]
        {
            "id:uuid:uuid:NO:",
            "org_id:uuid:uuid:NO:",
            "project_id:uuid:uuid:YES:",
            "period:text:text:NO:",
            "metric:text:text:NO:",
            "total_quantity:numeric:numeric:NO:",
            "total_amount:numeric:numeric:YES:",
            "currency:text:text:YES:",
            "row_count:integer:int4:NO:",
            "gap_flag:boolean:bool:NO:",
            "computed_at:timestamp with time zone:timestamptz:NO:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:",
        })
        {
            rollupColumns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"usage_rollup needs the plan §4 column shape '{prefix}'");
        }

        // TL T3 / 00 §5.4.6: every tenant table carries unique (org_id, id) as the composite-FK target.
        foreach (var table in UsageTables)
        {
            var constraints = await _fixture.ConstraintsAsync(database, table);

            constraints.ShouldContain(
                constraint => constraint.StartsWith($"u:{table}_org_id_key:", StringComparison.Ordinal)
                    && constraint.Contains("org_id, id", StringComparison.Ordinal),
                $"{table} needs unique (org_id, id) as {table}_org_id_key (TL T3)");
        }

        // T43 / AC1: the source-event dedupe key the batch insert arbitrates on.
        (await _fixture.ConstraintsAsync(database, "usage_ledger_entry")).ShouldContain(
            "u:usage_ledger_entry_billing_key_key:UNIQUE (org_id, billing_key)",
            "usage_ledger_entry needs unique (org_id, billing_key) as usage_ledger_entry_billing_key_key (T43)");

        // AC2 / T3: the composite self-reference and the composite project reference, not plain keys.
        var ledgerKeys = await _fixture.ConstraintNamesAsync(database, "usage_ledger_entry");
        ledgerKeys.ShouldContain("f:usage_ledger_entry_adjustment_fk");
        ledgerKeys.ShouldContain("f:usage_ledger_entry_project_fk");
        (await _fixture.ConstraintNamesAsync(database, "usage_rollup")).ShouldContain("f:usage_rollup_project_fk");

        // The two backstops behind the domain rules of RecordUsageBatch: a negative quantity only on
        // an adjustment row, and a 1-32 character unit. Both are named so the behaviour tests below
        // and the domain rules cite the same constraint.
        ledgerKeys.ShouldContain(
            "c:usage_ledger_entry_quantity_check",
            "a negative quantity must be refused by the database, not only by the domain rule (F-B)");
        ledgerKeys.ShouldContain(
            "c:usage_ledger_entry_unit_check",
            "unit needs a database bound of its own (F-D)");

        // §4 P4: one index serves every roll-up/summary/statement filter.
        (await _fixture.IndexesAsync(database, "usage_ledger_entry")).ShouldContain(
            index => index.StartsWith("idx_ledger_period:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id, period, metric)", StringComparison.Ordinal),
            "idx_ledger_period must be (org_id, project_id, period, metric) (plan §4, P4)");

        // 0047: the upsert target is the coalesced scope key, not a plain nullable four-column unique.
        var rollupIndexes = await _fixture.IndexesAsync(database, "usage_rollup");
        rollupIndexes.ShouldContain(
            index => index.StartsWith("usage_rollup_scope_key:", StringComparison.Ordinal)
                && index.Contains("UNIQUE INDEX", StringComparison.OrdinalIgnoreCase)
                && index.Contains("coalesce(project_id", StringComparison.OrdinalIgnoreCase),
            "usage_rollup_scope_key must be the unique index over coalesce(project_id, sentinel) that the "
            + "recompute upserts on (AC6)");
        rollupIndexes.ShouldContain(
            index => index.StartsWith("usage_rollup_project_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id)", StringComparison.Ordinal),
            "usage_rollup_project_idx must serve the composite project FK (0028 precedent)");

        // P-2: every roll-up row is rewritten by the recompute, so each page keeps headroom for the
        // new tuple version (a 100-fillfactor table would push every upsert to a new page).
        (await _fixture.ScalarAsync<string>(
            database,
            "select coalesce(array_to_string(reloptions, ','), '') from pg_catalog.pg_class "
            + "where oid = 'public.usage_rollup'::regclass"))
            .ShouldContain(
                "fillfactor=80",
                customMessage: "0047 creates usage_rollup with (fillfactor = 80) (P-2)");
    }

    [Fact]
    public async Task UsageTables_HaveForcedRls_AndTheAppScopedTenantPolicies()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var table in UsageTables)
        {
            var rls = await _fixture.RowSecurityAsync(database, table);

            rls.Enabled.ShouldBeTrue($"{table}: DECISIONS F5 requires ENABLE row level security");
            rls.Forced.ShouldBeTrue($"{table}: DECISIONS F5 requires FORCE row level security");

            // A policy with no `to` clause applies to PUBLIC; the tenant policy must name evidgate_app
            // only, and the permissive owner policy must name the migration role only (TL T76/F1, 0028).
            (await _fixture.PoliciesAsync(database, table)).ShouldBe(
            [
                $"{table}_owner_backfill:evidgate_owner:*:true:true",
                $"{table}_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
            ]);
        }
    }

    // ------------------------------------------------------------------ (c) AC3: append-only by grant

    [Fact]
    public async Task Ledger_UpdateDelete_Rejected()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var id = Guid.CreateVersion7();

        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var row = LedgerInsert(id, org, project, quantity: 3m);
            (await app.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1, "evidgate_app holds INSERT on the ledger");
            await app.CommitAsync();
        }

        // AC3: the app role's UPDATE and DELETE are refused at the ACL, before any policy or trigger.
        var update = await CaptureErrorAsync(
            _fixture.AppConnectionString(database),
            org,
            $"update public.usage_ledger_entry set quantity = 99 where id = '{id}'");
        update.SqlState.ShouldBe("42501", "UPDATE on the append-only ledger must be a permission error");

        var delete = await CaptureErrorAsync(
            _fixture.AppConnectionString(database),
            org,
            $"delete from public.usage_ledger_entry where id = '{id}'");
        delete.SqlState.ShouldBe("42501", "DELETE on the append-only ledger must be a permission error");

        // Neither refused statement changed anything.
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where id = '{id}' and quantity = 3 and version = 1"))
            .ShouldBe(1, "a refused UPDATE/DELETE must leave the row byte-identical");

        // The grants behind the refusal, read from the catalogue (0028/0034 precedent).
        foreach (var (role, privilege, expected) in new[]
        {
            ("evidgate_app", "SELECT", true),
            ("evidgate_app", "INSERT", true),
            ("evidgate_app", "UPDATE", false),
            ("evidgate_app", "DELETE", false),
            ("evidgate_app", "TRUNCATE", false),
            ("evidgate_operator", "SELECT", true),
            ("evidgate_worker_sched", "SELECT", false),
        })
        {
            (await _fixture.HasTablePrivilegeAsync(database, role, "usage_ledger_entry", privilege))
                .ShouldBe(expected, $"{role} {privilege} on usage_ledger_entry");
        }

        // The owner keeps the erasure/backfill path: its UPDATE reaches the row and the shared trigger
        // of 0004 moves the concurrency token (which is why the version column is not dead weight).
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), org))
        {
            (await owner.ExecuteAsync($"update public.usage_ledger_entry set quantity = 4 where id = '{id}'"))
                .ShouldBe(1);
            await owner.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where id = '{id}' and quantity = 4 and version = 2"))
            .ShouldBe(1, "the owner path updates the row and the 0004 trigger increments version");
    }
}
