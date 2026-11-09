using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The late-usage re-date of <c>usage_ledger_entry</c> against a real PostgreSQL: a ledger row — an adjustment row
/// included — metered for a period that holds a <c>billing_statement</c> is moved into the next open month, keeps the
/// month it was metered for in <c>original_period</c>, and never enters the issued month itself. The two sides
/// serialise on the same advisory key per candidate month, so a concurrent issue either blocks the walk or is seen
/// by it. A row for a month that is still open is written exactly as it was asked for.
/// <para>
/// Every insert runs as the role the case names (the app role inside a tenant-scoped transaction unless a case
/// deliberately speaks as the owner), so the guard is exercised through the same RLS path production uses. Races
/// observe the blocked backend through <c>pg_blocking_pids</c>; no case synchronises on a delay.
/// </para>
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class UsageLedgerIssuedPeriodTests
{
    private const string MigrationFile = "0094_usage_ledger_late_redate.sql";

    /// <summary>The migration whose guard this one replaces; its journal row must survive the replacement.</summary>
    private const string PriorMigration = "0087_usage_ledger_issued_period_guard.sql";

    /// <summary>The trigger both migrations attach to the ledger's rows.</summary>
    private const string Guard = "usage_ledger_entry_period_issued";

    private const string StatementTrigger = "billing_statement_period_lock";

    /// <summary>The named refusal of a row whose month and every month after it are issued.</summary>
    private const string NoOpenPeriod = "usage_ledger_entry_no_open_period";

    /// <summary>The named refusal of a row that names its own provenance while it is being moved.</summary>
    private const string SuppliedPeriod = "usage_ledger_entry_original_period_supplied";

    private readonly PostgresTenancyFixture _fixture;

    public UsageLedgerIssuedPeriodTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ (a) the migration

    [Fact]
    public async Task Schema_ReplacesThePriorGuard_AndIsJournalled()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var journal = await _fixture.ReadJournalAsync(database);
        journal.ShouldContain(row => row.Name == PriorMigration, "the re-date replaces the 0087 guard, it does not drop it");
        journal.ShouldContain(row => row.Name == MigrationFile, "the real runner must journal the re-date");
    }

    [Fact]
    public async Task Schema_OriginalPeriodColumn_IsNullableTextWithNoDefault()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // The column stays nullable with no default: every row that existed before the re-date keeps no provenance.
        (await _fixture.ScalarAsync<string>(
            database,
            """
            select data_type || '|' || is_nullable || '|' || coalesce(column_default, '-')
              from information_schema.columns
             where table_schema = 'public' and table_name = 'usage_ledger_entry'
               and column_name = 'original_period'
            """))
            .ShouldBe("text|YES|-");
    }

    [Fact]
    public async Task Schema_OriginalPeriodChecks_AreAttachedOnceAndValidated()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var constraint in new[]
        {
            "usage_ledger_entry_original_period_check",
            "usage_ledger_entry_original_period_order_check",
        })
        {
            (await _fixture.ScalarAsync<long>(
                database,
                "select count(*) from pg_catalog.pg_constraint "
                + $"where conrelid = 'public.usage_ledger_entry'::regclass and conname = '{constraint}'"))
                .ShouldBe(1, $"the ledger must carry exactly one {constraint}");

            // Each check is added `not valid` and validated in the same migration, so trusting one is what proves the
            // validate ran instead of leaving the constraint unenforced on every row that predates it.
            (await _fixture.ScalarAsync<bool>(
                database,
                "select convalidated from pg_catalog.pg_constraint "
                + $"where conrelid = 'public.usage_ledger_entry'::regclass and conname = '{constraint}'"))
                .ShouldBeTrue($"{constraint} must be validated, not left `not valid`");
        }
    }

    [Fact]
    public async Task Schema_BothGuards_AreAttachedOnce()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var (table, trigger) in new[]
        {
            ("usage_ledger_entry", Guard),
            ("billing_statement", StatementTrigger),
        })
        {
            (await _fixture.ScalarAsync<long>(
                database,
                "select count(*) from pg_catalog.pg_trigger "
                + $"where tgrelid = 'public.{table}'::regclass and tgname = '{trigger}' and not tgisinternal"))
                .ShouldBe(1, $"{table} must carry exactly one {trigger} trigger");
        }
    }

    [Fact]
    public async Task Schema_GuardFunctions_AreInvokerSearchPathPinnedAndClosed()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // Both bodies run as their invoker (the app role sees its own org's statement through its tenant policy,
        // the owner through the permissive backfill policy), pin an empty search_path and are closed to PUBLIC.
        (await _fixture.ScalarAsync<long>(
            database,
            """
            select count(*)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate'
               and p.proname in ('usage_ledger_entry_period_issued_guard', 'billing_statement_period_lock')
               and not p.prosecdef
               and array_position(p.proconfig, 'search_path=""') is not null
               and not exists (
                 select 1 from aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                  where a.grantee = 0 and a.privilege_type = 'EXECUTE')
            """))
            .ShouldBe(2, "both guard functions are invoker, search_path-pinned and not executable by PUBLIC");
    }

    [Fact]
    public async Task Schema_GuardFunctions_GrantNoExecuteToTheAppRole()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var function in new[]
        {
            "evidgate.usage_ledger_entry_period_issued_guard()",
            "evidgate.billing_statement_period_lock()",
        })
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_function_privilege('evidgate_app', '{function}', 'EXECUTE')"))
                .ShouldBeFalse($"{function} is called by its trigger, so the app role needs no EXECUTE on it");
        }
    }

    [Fact]
    public async Task Migration_ReappliedAfterItsJournalRowIsRemoved_ReplacesItsObjects()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await _fixture.ExecuteAsync(
            database,
            $"delete from public.schema_migration where name = '{MigrationFile}'");

        var again = _fixture.ApplyMigrations(database);

        again.ExitCode.ShouldBe(MigratorExitCodes.Success, again.Transcript);
        again.Result.AppliedNow.ShouldBe([MigrationFile]);

        foreach (var (table, trigger) in new[]
        {
            ("usage_ledger_entry", Guard),
            ("billing_statement", StatementTrigger),
        })
        {
            (await _fixture.ScalarAsync<long>(
                database,
                "select count(*) from pg_catalog.pg_trigger "
                + $"where tgrelid = 'public.{table}'::regclass and tgname = '{trigger}' and not tgisinternal"))
                .ShouldBe(1, $"a re-run of {MigrationFile} must leave one {trigger} trigger, never two");
        }

        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from information_schema.columns "
            + "where table_schema = 'public' and table_name = 'usage_ledger_entry' and column_name = 'original_period'"))
            .ShouldBe(1, "the re-run adds the column once");

        (await _fixture.ReadJournalAsync(database)).Count(row => row.Name == MigrationFile)
            .ShouldBe(1, "the re-run is journalled once");
    }

    // ------------------------------------------------------------------ (b) the re-date and its controls

    [Fact]
    public async Task Ledger_InsertIntoIssuedPeriod_RedatedIntoTheNextOpenMonth()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await IssueAsAppAsync(world, world.Org, "2026-09");

        // Control 1: the same org's open period holds no statement, so its row is written exactly as asked for.
        await using (var open = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(world.Org, world.Project, "2026-10", Unique.Slug("open"));
            (await open.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1, "an open period accepts a ledger row");
            await open.CommitAsync();
        }

        const string Late = "late-row";
        await using (var issued = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(world.Org, world.Project, "2026-09", Late);
            (await issued.ExecuteAsync(row.Sql, row.Parameters))
                .ShouldBe(1, "a late row is billed in the next open month, never refused");
            await issued.CommitAsync();
        }

        (await RowPeriodAsync(world, Late)).ShouldBe("2026-10|2026-09");

        // Control 2: another org's identical period has issued nothing, so its row stays where it was metered.
        const string Other = "other-row";
        await using (var other = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            var row = LedgerRow(otherOrg, otherProject, "2026-09", Other);
            (await other.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1, "the guard is per tenant and period");
            await other.CommitAsync();
        }

        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}' and period = '2026-09'"))
            .ShouldBe(0, "no row may land in the issued month, not even the one that was moved out of it");
        (await world.ScalarAsync<long>(
            $"select count(*) from public.billing_statement where org_id = '{world.Org:D}' and period = '2026-09'"))
            .ShouldBe(1, "the zero above is the guard, not a missing statement");
    }

    [Fact]
    public async Task Ledger_ConsecutiveIssuedMonths_SkipsToTheFirstOpenOne()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await IssueAsAppAsync(world, world.Org, "2026-09");
        await IssueAsAppAsync(world, world.Org, "2026-10");

        const string Late = "chained-row";
        await using (var issued = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(world.Org, world.Project, "2026-09", Late);
            (await issued.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await issued.CommitAsync();
        }

        (await RowPeriodAsync(world, Late))
            .ShouldBe("2026-11|2026-09", "every issued month in the chain is skipped, not just the requested one");
    }

    [Fact]
    public async Task Ledger_AdjustmentIntoIssuedPeriod_RedatedToo()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);

        var original = Guid.CreateVersion7();
        await using (var open = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(world.Org, world.Project, "2026-10", Unique.Slug("adjusted"), original);
            (await open.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await open.CommitAsync();
        }

        await IssueAsAppAsync(world, world.Org, "2026-09");

        // A correction is a row too: metered for the issued month it is billed in the open one, like a plain row.
        const string Correction = "correction-row";
        await using (var issued = await world.BeginAppScopeAsync())
        {
            var row = AdjustmentRow(world.Org, world.Project, "2026-09", Correction, original);
            (await issued.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await issued.CommitAsync();
        }

        (await RowPeriodAsync(world, Correction)).ShouldBe("2026-10|2026-09");
        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}' and reason is null"))
            .ShouldBe(1, "only the seeded row carries no reason");
    }

    [Fact]
    public async Task Ledger_OwnerInsertIntoIssuedPeriod_RedatedToo()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var owner = _fixture.OwnerConnectionString(world.Database);

        await using (var issue = await TenantScope.BeginAsync(owner, world.Org))
        {
            var statement = StatementRow(world.Org, "2026-08");
            (await issue.ExecuteAsync(statement.Sql, statement.Parameters)).ShouldBe(1);
            await issue.CommitAsync();
        }

        // The guard binds the backfill path too: the owner policy exposes the statement, so the walk finds it.
        const string Late = "owner-row";
        await using (var issued = await TenantScope.BeginAsync(owner, world.Org))
        {
            var row = LedgerRow(world.Org, world.Project, "2026-08", Late);
            (await issued.ExecuteAsync(row.Sql, row.Parameters))
                .ShouldBe(1, "no role bypasses the guard by holding the owner's permissive policy");
            await issued.CommitAsync();
        }

        (await RowPeriodAsync(world, Late)).ShouldBe("2026-09|2026-08");
    }

    [Fact]
    public async Task Ledger_NoOpenMonthAfterTheLastPeriod_RefusedWithTheNamedReason()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await IssueAsAppAsync(world, world.Org, "9999-12");

        // The calendar ends at 9999-12, so there is no month left to bill: the walk refuses under its own name instead
        // of writing into the billed month.
        await using (var issued = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(world.Org, world.Project, "9999-12", Unique.Slug("last"));
            var error = await issued.CaptureErrorAsync(row.Sql, row.Parameters);

            error.SqlState.ShouldBe("23514");
            error.ConstraintName.ShouldBe(NoOpenPeriod);
            error.MessageText.Contains("9999-12", StringComparison.Ordinal)
                .ShouldBeTrue("the reason names the period whose chain is exhausted");
        }

        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}'"))
            .ShouldBe(0, "a row that cannot be billed anywhere is never written");
    }

    // ------------------------------------------------------------------ (c) the race with an issue

    [Fact]
    public async Task Issue_WhileLedgerInsertIsInFlight_WaitsForIt()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        const string Period = "2026-11";

        await using var ledger = await world.BeginAppScopeAsync();
        var holderPid = await ledger.ScalarAsync<int>("select pg_backend_pid()");
        var row = LedgerRow(world.Org, world.Project, Period, Unique.Slug("race"));
        (await ledger.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);

        await using var issue = await world.BeginAppScopeAsync();
        var statement = StatementRow(world.Org, Period);
        var pending = issue.ExecuteAsync(statement.Sql, statement.Parameters);

        await world.WaitForBlockedByAsync(holderPid);
        pending.IsCompleted.ShouldBeFalse("the issue waits for the ledger insert's shared lock");

        await ledger.CommitAsync();

        (await pending).ShouldBe(1, "the issue proceeds once the in-flight ledger insert commits");
        await issue.CommitAsync();

        (await world.ScalarAsync<long>(
            $"select count(*) from public.billing_statement where org_id = '{world.Org:D}' and period = '{Period}'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Ledger_WhileIssueIsInFlight_BlocksThenLandsInTheMonthAfterIt()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        const string Period = "2026-12";

        await using var issue = await world.BeginAppScopeAsync();
        var holderPid = await issue.ScalarAsync<int>("select pg_backend_pid()");
        var statement = StatementRow(world.Org, Period);
        (await issue.ExecuteAsync(statement.Sql, statement.Parameters)).ShouldBe(1);

        const string Late = "blocked-row";
        await using var ledger = await world.BeginAppScopeAsync();
        var row = LedgerRow(world.Org, world.Project, Period, Late);
        var pending = ledger.ExecuteAsync(row.Sql, row.Parameters);

        await world.WaitForBlockedByAsync(holderPid);
        pending.IsCompleted.ShouldBeFalse("the ledger insert waits for the statement's exclusive lock");

        await issue.CommitAsync();

        (await pending).ShouldBe(1, "the row is written once the lock is released, in the month that follows");
        await ledger.CommitAsync();

        (await RowPeriodAsync(world, Late))
            .ShouldBe("2027-01|2026-12", "the insert re-reads the issued month and moves out of it");
        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}' and period = '{Period}'"))
            .ShouldBe(0, "the blocked insert must not slip a row into the issued period");
    }

    // ------------------------------------------------------------------ helpers

    private const string LedgerSql =
        """
        insert into public.usage_ledger_entry
            (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at, original_period)
        values (@id, @org_id, @project_id, 'results', 1, 'count', @period, @billing_key, now(), @original_period)
        """;

    private const string AdjustmentSql =
        """
        insert into public.usage_ledger_entry
            (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at,
             adjustment_of, reason)
        values (@id, @org_id, @project_id, 'results', -1, 'count', @period, @billing_key, now(),
                @adjustment_of, 'correction')
        """;

    private const string StatementSql =
        """
        insert into public.billing_statement
            (id, org_id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json,
             issued_by, issued_at)
        values (@id, @org_id, @period, 'invoice', 0, 'USD', 0, repeat('a', 64), '{}'::jsonb,
                @issued_by, now())
        """;

    /// <summary>One plain ledger row; every other member takes its default or a value the CHECKs accept.</summary>
    /// <param name="org">The tenant.</param>
    /// <param name="project">The row's project.</param>
    /// <param name="period">The period the writer meters for.</param>
    /// <param name="billingKey">The row's key.</param>
    /// <param name="id">The row id, when a caller needs it up front.</param>
    /// <param name="originalPeriod">A provenance a writer supplies itself, which the guard refuses while moving the row.</param>
    private static (string Sql, (string Name, object Value)[] Parameters) LedgerRow(
        Guid org,
        Guid project,
        string period,
        string billingKey,
        Guid? id = null,
        string? originalPeriod = null) =>
        (LedgerSql,
        [
            ("id", id ?? Guid.CreateVersion7()),
            ("org_id", org),
            ("project_id", project),
            ("period", period),
            ("billing_key", billingKey),
            ("original_period", (object?)originalPeriod ?? DBNull.Value),
        ]);

    /// <summary>One adjustment row naming the row it corrects; the reason CHECK demands the reason.</summary>
    private static (string Sql, (string Name, object Value)[] Parameters) AdjustmentRow(
        Guid org,
        Guid project,
        string period,
        string billingKey,
        Guid adjustmentOf) =>
        (AdjustmentSql,
        [
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("project_id", project),
            ("period", period),
            ("billing_key", billingKey),
            ("adjustment_of", adjustmentOf),
        ]);

    /// <summary>One statement row for a period: the document the guard reads.</summary>
    private static (string Sql, (string Name, object Value)[] Parameters) StatementRow(Guid org, string period) =>
        (StatementSql,
        [
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("period", period),
            ("issued_by", Guid.CreateVersion7()),
        ]);

    /// <summary>The stored <c>period|original_period</c> of <paramref name="billingKey"/>'s row.</summary>
    private static async Task<string?> RowPeriodAsync(UsageQuotaWorld world, string billingKey) =>
        await world.ScalarAsync<string>(
            $"select period || '|' || coalesce(original_period, '-') from public.usage_ledger_entry "
            + $"where org_id = '{world.Org:D}' and billing_key = '{billingKey}'");

    /// <summary>Issues a period as the app role inside its tenant transaction: what the route does.</summary>
    private async Task IssueAsAppAsync(UsageQuotaWorld world, Guid org, string period)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        var statement = StatementRow(org, period);
        (await scope.ExecuteAsync(statement.Sql, statement.Parameters)).ShouldBe(1, $"{period} must be issuable");
        await scope.CommitAsync();
    }
}
