using System.IO;
using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 12.3.1-T1.1/T1.2 against a real PostgreSQL: migration 0046's append-only <c>usage_ledger_entry</c>
/// and migration 0047's <c>usage_rollup</c> — the tenant keys, the dedupe unique key (AC1, T43), the
/// composite adjustment self-reference and the reason CHECK (AC2), the app role's missing UPDATE and
/// DELETE (AC3, C6), the plan §4 closed-list CHECKs (metric, period, billing_key) plus the additive
/// quantity and unit bounds the plan does not state, the roll-up's both uniques and
/// its coalesce-index upsert target (AC6, NFR-12-1), FORCE RLS with the tenant policy (T2, T3) and the
/// deploy path onto a database that already holds data.
/// <para>
/// The fixture is this suite's own <see cref="PostgresTenancyFixture"/> — one digest-pinned PostgreSQL
/// 18 container per assembly, a fresh database per test brought to head by the real
/// <c>Evidgate.Migrator</c> — the same precedent <c>EvidenceArtifactSchemaTests</c> (0028) and
/// <c>AuthoringSchemaTests</c> (0039) follow for a new table. Every write runs as <c>evidgate_app</c>
/// inside a tenant-scoped transaction (00 §5.5 C3) unless a case deliberately speaks as the owner (the
/// migration/backfill role) or as the operator.
/// </para>
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class UsageLedgerSchemaTests
{
    /// <summary>The first usage migration; the forward-apply case stops just before it.</summary>
    private const string LedgerMigrationFile = "0046_usage_ledger.sql";

    /// <summary>The roll-up migration, applied by the same forward step.</summary>
    private const string RollupMigrationFile = "0047_usage_rollup.sql";

    /// <summary>The two tables this story's schema owns, in dependency order.</summary>
    private static readonly string[] UsageTables = ["usage_ledger_entry", "usage_rollup"];

    /// <summary>The sentinel the roll-up's unique index substitutes for a NULL <c>project_id</c>.</summary>
    private const string NullProjectSentinel = "00000000-0000-0000-0000-000000000000";

    private readonly PostgresTenancyFixture _fixture;

    public UsageLedgerSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ (a) migrations apply

    [Fact]
    public async Task Migrations_ApplyOnAFreshDatabase_BothFilesJournalled()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
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

    // ------------------------------------------------------------------ (d) AC1: the dedupe key

    [Fact]
    public async Task Ledger_DuplicateBillingKey_RaisesUniqueViolation()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        var first = LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: "batch:7f6a:results");
        (await app.ExecuteAsync(first.Sql, first.Parameters)).ShouldBe(1);

        // A plain INSERT of the same source event is the caller's bug, and the database says so
        // rather than billing twice; the batch path below is the tolerant form.
        var second = LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: "batch:7f6a:results");
        var error = await app.CaptureErrorAsync(second.Sql, second.Parameters);

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("usage_ledger_entry_billing_key_key");
    }

    [Fact]
    public async Task Ledger_DuplicateBillingKey_ConcurrentOnConflictInserts_LeaveOneRow()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        const int Writers = 8;
        const string BillingKey = "batch:race:results";

        // AC1 through the production statement shape: one `insert … on conflict (org_id, billing_key)
        // do nothing returning billing_key` per batch, all racing the same source event. Exactly one
        // connection may insert; every other one reports nothing back, and the table holds one row.
        var inserted = await Task.WhenAll(Enumerable.Range(0, Writers).Select(async _ =>
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
            var row = LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: BillingKey);
            var affected = await scope.ExecuteAsync(
                row.Sql + " on conflict (org_id, billing_key) do nothing",
                row.Parameters);
            await scope.CommitAsync();

            return affected;
        }));

        inserted.Sum().ShouldBe(1, $"{Writers} concurrent batches with one billing_key must insert exactly one row");
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}' and billing_key = '{BillingKey}'"))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ (e) AC2: adjustments

    [Fact]
    public async Task Ledger_Adjustment_LeavesTheOriginalIdentical_AndBothRowsAppear()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var originalId = Guid.CreateVersion7();

        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        var original = LedgerInsert(originalId, org, project, quantity: 120m, billingKey: "batch:ac2:results");
        (await app.ExecuteAsync(original.Sql, original.Parameters)).ShouldBe(1);

        var before = await app.ScalarAsync<string>(
            $"select row_to_json(t)::text from public.usage_ledger_entry t where id = '{originalId}'");

        var adjustment = LedgerInsert(
            Guid.CreateVersion7(),
            org,
            project,
            quantity: -20m,
            billingKey: "adjustment:ac2:results",
            adjustmentOf: originalId,
            reason: "24 events were duplicates of batch 6f1c");
        (await app.ExecuteAsync(adjustment.Sql, adjustment.Parameters))
            .ShouldBe(1, "a same-org adjustment is a new row pointing at the corrected one");

        var after = await app.ScalarAsync<string>(
            $"select row_to_json(t)::text from public.usage_ledger_entry t where id = '{originalId}'");

        after.ShouldBe(before, "AC2: the corrected row is byte-identical after the adjustment is inserted");

        // Both rows are in the period statement: the sum is what the invoice reconciles to.
        (await app.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}' "
            + "and period = '2026-10' and metric = 'results'"))
            .ShouldBe(2);
        (await app.ScalarAsync<decimal>(
            $"select sum(quantity) from public.usage_ledger_entry where org_id = '{org}' "
            + "and period = '2026-10' and metric = 'results'"))
            .ShouldBe(100m);
    }

    [Fact]
    public async Task Ledger_Adjustment_CrossTenantParent_Rejected()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var (otherOrg, otherProject) = await SeedTenantAsync(database, "beta", "store");
        var foreignRow = Guid.CreateVersion7();

        await using (var other = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), otherOrg))
        {
            var row = LedgerInsert(foreignRow, otherOrg, otherProject, billingKey: "batch:other:results");
            (await other.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await other.CommitAsync();
        }

        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        // (org_id, adjustment_of) is a composite key, so naming another tenant's row cannot even be
        // expressed as a valid reference: the FK answers 23503 for the whole key.
        var corrupt = LedgerInsert(
            Guid.CreateVersion7(),
            org,
            project,
            billingKey: "adjustment:corrupt:results",
            adjustmentOf: foreignRow,
            reason: "points at another tenant");
        var error = await app.CaptureErrorAsync(corrupt.Sql, corrupt.Parameters);

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("usage_ledger_entry_adjustment_fk");
    }

    [Fact]
    public async Task Ledger_Adjustment_RequiresAReason()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var originalId = Guid.CreateVersion7();

        var original = LedgerInsert(originalId, org, project, billingKey: "batch:reason:results");
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            // A plain ledger row needs no reason: `reason` is only constrained on an adjustment.
            (await app.ExecuteAsync(original.Sql, original.Parameters)).ShouldBe(1);
            await app.CommitAsync();
        }

        foreach (var (label, reason) in new[]
        {
            ("missing", (string?)null),
            ("empty", string.Empty),
            ("over-long", new string('r', 2001)),
        })
        {
            await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
            var adjustment = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                billingKey: $"adjustment:{label}:results",
                adjustmentOf: originalId,
                reason: reason);
            var error = await app.CaptureErrorAsync(adjustment.Sql, adjustment.Parameters);

            error.SqlState.ShouldBe("23514", $"an adjustment with a {label} reason must be refused");
            error.ConstraintName.ShouldBe("usage_ledger_entry_adjustment_reason_check");
        }
    }

    // ------------------------------------------------------------------ (f) the closed lists and bounds

    [Fact]
    public async Task Ledger_CheckBoundaries_RejectUnknownMetricPeriodAndBillingKey()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        var cases = new (string Label, (string Sql, (string Name, object Value)[] Parameters) Insert, string Constraint)[]
        {
            ("unknown metric",
                LedgerInsert(Guid.CreateVersion7(), org, project, metric: "storage_gb"),
                "usage_ledger_entry_metric_check"),
            ("month 13",
                LedgerInsert(Guid.CreateVersion7(), org, project, period: "2026-13"),
                "usage_ledger_entry_period_check"),
            ("unpadded month",
                LedgerInsert(Guid.CreateVersion7(), org, project, period: "2026-1"),
                "usage_ledger_entry_period_check"),
            ("empty billing key",
                LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: string.Empty),
                "usage_ledger_entry_billing_key_check"),
            ("201-character billing key",
                LedgerInsert(Guid.CreateVersion7(), org, project, billingKey: new string('k', 201)),
                "usage_ledger_entry_billing_key_check"),
        };

        foreach (var (label, insert, constraint) in cases)
        {
            await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
            var error = await app.CaptureErrorAsync(insert.Sql, insert.Parameters);

            error.SqlState.ShouldBe("23514", $"{label} must be refused by {constraint}");
            error.ConstraintName.ShouldBe(constraint);
        }

        // The boundaries themselves are inside the contract: January, and a 200-character key.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var edge = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                period: "2026-01",
                billingKey: new string('k', 200));
            (await app.ExecuteAsync(edge.Sql, edge.Parameters)).ShouldBe(1, "the inclusive bounds are legal rows");
            await app.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.usage_ledger_entry")).ShouldBe(1);
    }

    [Fact]
    public async Task Ledger_NegativeQuantity_OnlyOnAnAdjustmentRow()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        // F-B: a plain metering row cannot carry a negative quantity. The domain rule refuses it with
        // 422 first; this CHECK is the backstop for every writer that reaches SQL directly.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var negative = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                quantity: -1m,
                billingKey: "batch:negative:results");
            var error = await app.CaptureErrorAsync(negative.Sql, negative.Parameters);

            error.SqlState.ShouldBe("23514", "a negative quantity on a plain ledger row must be refused");
            error.ConstraintName.ShouldBe("usage_ledger_entry_quantity_check");
        }

        var originalId = Guid.CreateVersion7();
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            var original = LedgerInsert(originalId, org, project, quantity: 40m, billingKey: "batch:correction:results");
            (await app.ExecuteAsync(original.Sql, original.Parameters)).ShouldBe(1);

            // AC2: a correction subtracts, so a negative quantity is legal exactly here.
            var correction = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                quantity: -40m,
                billingKey: "adjustment:correction:results",
                adjustmentOf: originalId,
                reason: "the batch was rejected after it had been billed");
            (await app.ExecuteAsync(correction.Sql, correction.Parameters))
                .ShouldBe(1, "a negative quantity is the point of an adjustment row");

            // The CHECK bounds the plain rows only: a positive adjustment is equally legal.
            var topUp = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                quantity: 5m,
                billingKey: "adjustment:topup:results",
                adjustmentOf: originalId,
                reason: "five accepted events were missing from the batch");
            (await app.ExecuteAsync(topUp.Sql, topUp.Parameters)).ShouldBe(1);
            await app.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}'"))
            .ShouldBe(3, "the refused negative row wrote nothing; the original and both adjustments did");
        (await _fixture.ScalarAsync<decimal>(
            database,
            $"select sum(quantity) from public.usage_ledger_entry where org_id = '{org}'"))
            .ShouldBe(5m, "40 - 40 + 5 is what the period statement reconciles to");
    }

    [Fact]
    public async Task Ledger_Unit_BoundedToOneTo32Characters()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        var refused = new (string Label, string Unit)[]
        {
            ("empty", string.Empty),
            ("33 ASCII characters", new string('u', 33)),
            // char_length counts characters, not bytes: 33 two-byte characters are still 33.
            ("33 two-byte characters", new string('\u00e9', 33)),
        };

        foreach (var (label, unit) in refused)
        {
            await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
            var row = LedgerInsert(
                Guid.CreateVersion7(),
                org,
                project,
                unit: unit,
                billingKey: $"batch:unit:{label}");
            var error = await app.CaptureErrorAsync(row.Sql, row.Parameters);

            error.SqlState.ShouldBe("23514", $"a {label} unit must be refused");
            error.ConstraintName.ShouldBe("usage_ledger_entry_unit_check");
        }

        // The inclusive bounds are legal rows, including a multibyte unit at the limit.
        foreach (var (label, unit) in new[]
        {
            ("one", "u"),
            ("32 ASCII characters", new string('u', 32)),
            ("32 two-byte characters", new string('\u00e9', 32)),
        })
        {
            await ExecuteAsync(
                _fixture.AppConnectionString(database),
                org,
                LedgerInsert(Guid.CreateVersion7(), org, project, unit: unit, billingKey: $"batch:unit:ok:{label}"));
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{org}'"))
            .ShouldBe(3, "only the three in-bound rows exist; every refused unit wrote nothing");
    }

    // ------------------------------------------------------------------ (g) T1.2: the roll-up

    [Fact]
    public async Task Rollup_Uniqueness_CountsANullProjectAsOneScope()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
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

    // ------------------------------------------------------------------ helpers

    private const string LedgerColumns =
        "(id, org_id, metric, quantity, unit, period, billing_key, occurred_at";

    /// <summary>
    /// One ledger INSERT as the production batch statement shape. Optional columns are appended only
    /// when set, so a NULL never needs a typed parameter (00 §5.7 S1: values stay parameterised).
    /// </summary>
    private static (string Sql, (string Name, object Value)[] Parameters) LedgerInsert(
        Guid id,
        Guid org,
        Guid? project,
        string metric = "results",
        decimal quantity = 1m,
        string unit = "count",
        string period = "2026-10",
        string billingKey = "batch:1:results",
        Guid? adjustmentOf = null,
        string? reason = null,
        Guid? priceRef = null,
        decimal? amount = null,
        string? currency = null)
    {
        var columns = LedgerColumns + (project is null ? string.Empty : ", project_id");
        var values = "values (@id, @org_id, @metric, @quantity, @unit, @period, @billing_key, @occurred_at"
            + (project is null ? string.Empty : ", @project_id");
        var parameters = new List<(string Name, object Value)>
        {
            ("id", id),
            ("org_id", org),
            ("metric", metric),
            ("quantity", quantity),
            ("unit", unit),
            ("period", period),
            ("billing_key", billingKey),
            ("occurred_at", DateTimeOffset.UtcNow),
        };

        if (project is { } projectId)
        {
            parameters.Add(("project_id", projectId));
        }

        if (priceRef is { } price)
        {
            columns += ", price_ref";
            values += ", @price_ref";
            parameters.Add(("price_ref", price));
        }

        if (amount is { } amountValue)
        {
            columns += ", amount";
            values += ", @amount";
            parameters.Add(("amount", amountValue));
        }

        if (currency is { } currencyCode)
        {
            columns += ", currency";
            values += ", @currency";
            parameters.Add(("currency", currencyCode));
        }

        if (adjustmentOf is { } parent)
        {
            columns += ", adjustment_of";
            values += ", @adjustment_of";
            parameters.Add(("adjustment_of", parent));
        }

        if (reason is { } adjustmentReason)
        {
            columns += ", reason";
            values += ", @reason";
            parameters.Add(("reason", adjustmentReason));
        }

        return ($"insert into public.usage_ledger_entry {columns}) {values})", parameters.ToArray());
    }

    /// <summary>One roll-up INSERT; the caller appends the recompute's <c>on conflict</c> clause.</summary>
    private static (string Sql, (string Name, object Value)[] Parameters) RollupInsert(
        Guid id,
        Guid org,
        Guid? projectId,
        decimal totalQuantity,
        int rowCount,
        string period = "2026-10",
        string metric = "results")
    {
        var columns = "(id, org_id, period, metric, total_quantity, row_count, computed_at"
            + (projectId is null ? string.Empty : ", project_id");
        var values = "values (@id, @org_id, @period, @metric, @total_quantity, @row_count, @computed_at"
            + (projectId is null ? string.Empty : ", @project_id");
        var parameters = new List<(string Name, object Value)>
        {
            ("id", id),
            ("org_id", org),
            ("period", period),
            ("metric", metric),
            ("total_quantity", totalQuantity),
            ("row_count", rowCount),
            ("computed_at", DateTimeOffset.UtcNow),
        };

        if (projectId is { } scope)
        {
            parameters.Add(("project_id", scope));
        }

        return ($"insert into public.usage_rollup {columns}) {values})", parameters.ToArray());
    }

    /// <summary>An org and a project in it, written as the migration role.</summary>
    private async Task<(Guid Org, Guid Project)> SeedTenantAsync(string database, string orgSlug, string projectSlug)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", orgSlug),
            ("slug", orgSlug));
        await seed.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", project),
            ("org_id", org),
            ("name", projectSlug),
            ("slug", projectSlug));
        await seed.CommitAsync();

        return (org, project);
    }

    /// <summary>Runs one INSERT as the app role in a fresh tenant transaction and commits exactly one row.</summary>
    private static async Task ExecuteAsync(
        string connectionString,
        Guid org,
        (string Sql, (string Name, object Value)[] Parameters) insert)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);

        (await scope.ExecuteAsync(insert.Sql, insert.Parameters)).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>Runs one statement in a fresh transaction for <paramref name="connectionString"/> and returns the server error.</summary>
    private static async Task<PostgresException> CaptureErrorAsync(
        string connectionString,
        Guid org,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);

        return await scope.CaptureErrorAsync(sql, parameters);
    }
}
