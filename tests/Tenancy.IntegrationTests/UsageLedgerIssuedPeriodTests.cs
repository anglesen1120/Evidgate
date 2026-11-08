using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The issued-period guard of <c>usage_ledger_entry</c> against a real PostgreSQL: a ledger row — an adjustment
/// row included — cannot enter a period that holds a <c>billing_statement</c>, whoever writes it, and the two
/// sides serialise on the same advisory key so a concurrent issue and ledger insert cannot both succeed.
/// <para>
/// Every insert runs as the role the case names (the app role inside a tenant-scoped transaction unless a case
/// deliberately speaks as the owner), so the guard is exercised through the same RLS path production uses. Races
/// observe the blocked backend through <c>pg_blocking_pids</c>; no case synchronises on a delay.
/// </para>
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class UsageLedgerIssuedPeriodTests
{
    private const string MigrationFile = "0087_usage_ledger_issued_period_guard.sql";

    /// <summary>
    /// The trigger's name and, deliberately, the constraint label its refusal carries: one string a caller can
    /// map a 23514 back to the issued period from.
    /// </summary>
    private const string Guard = "usage_ledger_entry_period_issued";

    private const string StatementTrigger = "billing_statement_period_lock";

    private readonly PostgresTenancyFixture _fixture;

    public UsageLedgerIssuedPeriodTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ (a) the migration

    [Fact]
    public async Task Schema_GuardJournalledAndAttached_WithOwnerOnlyFunctions()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.ReadJournalAsync(database))
            .ShouldContain(row => row.Name == MigrationFile, "the real runner must journal the guard");

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

        (await _fixture.ReadJournalAsync(database)).Count(row => row.Name == MigrationFile)
            .ShouldBe(1, "the re-run is journalled once");
    }

    // ------------------------------------------------------------------ (b) refusal and its controls

    [Fact]
    public async Task Ledger_InsertIntoIssuedPeriod_Refused()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await IssueAsAppAsync(world, world.Org, "2026-09");

        // Positive control 1: the same org's open period holds no statement, so its row is written.
        await using (var open = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(world.Org, world.Project, "2026-10", Unique.Slug("open"));
            (await open.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1, "an open period accepts a ledger row");
            await open.CommitAsync();
        }

        await using (var issued = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(world.Org, world.Project, "2026-09", Unique.Slug("issued"));
            var error = await issued.CaptureErrorAsync(row.Sql, row.Parameters);

            error.SqlState.ShouldBe("23514", "an issued period is closed to new ledger rows");
            error.ConstraintName.ShouldBe(Guard, "the refusal names the guard, so a caller can map it");
            error.MessageText.Contains("2026-09", StringComparison.Ordinal).ShouldBeTrue("the message names the period");
        }

        // Positive control 2: another org's identical period has issued nothing, so its row is written.
        await using (var other = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            var row = LedgerRow(otherOrg, otherProject, "2026-09", Unique.Slug("other"));
            (await other.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1, "the guard is per tenant and period");
            await other.CommitAsync();
        }

        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}' and period = '2026-09'"))
            .ShouldBe(0, "the refused insert must leave no row behind");

        (await world.ScalarAsync<long>(
            $"select count(*) from public.billing_statement where org_id = '{world.Org:D}' and period = '2026-09'"))
            .ShouldBe(1, "the zero above is the guard, not a missing statement");
    }

    [Fact]
    public async Task Ledger_AdjustmentIntoIssuedPeriod_Refused()
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

        // A correction is a row too: an adjustment cannot reach an issued period either.
        await using (var issued = await world.BeginAppScopeAsync())
        {
            var row = AdjustmentRow(world.Org, world.Project, "2026-09", Unique.Slug("fix"), original);
            (await issued.CaptureErrorAsync(row.Sql, row.Parameters)).ConstraintName.ShouldBe(Guard);
        }

        // The same correction into an open period is the sanctioned path, so the refusal above is the period.
        await using (var open = await world.BeginAppScopeAsync())
        {
            var row = AdjustmentRow(world.Org, world.Project, "2026-10", Unique.Slug("fix"), original);
            (await open.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1, "a correction lands in an open period");
            await open.CommitAsync();
        }

        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}' and reason is not null"))
            .ShouldBe(1, "only the open-period correction was written");
    }

    [Fact]
    public async Task Ledger_OwnerInsertIntoIssuedPeriod_Refused()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var owner = _fixture.OwnerConnectionString(world.Database);

        await using (var issue = await TenantScope.BeginAsync(owner, world.Org))
        {
            var statement = StatementRow(world.Org, "2026-08");
            (await issue.ExecuteAsync(statement.Sql, statement.Parameters)).ShouldBe(1);
            await issue.CommitAsync();
        }

        // The guard binds the backfill path too: the owner policy exposes the statement, so the probe finds it.
        await using (var issued = await TenantScope.BeginAsync(owner, world.Org))
        {
            var row = LedgerRow(world.Org, world.Project, "2026-08", Unique.Slug("owner"));
            (await issued.CaptureErrorAsync(row.Sql, row.Parameters)).ConstraintName
                .ShouldBe(Guard, "no role bypasses the guard by holding the owner's permissive policy");
        }

        await using (var open = await TenantScope.BeginAsync(owner, world.Org))
        {
            var row = LedgerRow(world.Org, world.Project, "2026-10", Unique.Slug("owner"));
            (await open.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1, "the owner still writes open periods");
            await open.CommitAsync();
        }
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
    public async Task Ledger_WhileIssueIsInFlight_BlocksThenRefused()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        const string Period = "2026-12";

        await using var issue = await world.BeginAppScopeAsync();
        var holderPid = await issue.ScalarAsync<int>("select pg_backend_pid()");
        var statement = StatementRow(world.Org, Period);
        (await issue.ExecuteAsync(statement.Sql, statement.Parameters)).ShouldBe(1);

        await using var ledger = await world.BeginAppScopeAsync();
        var row = LedgerRow(world.Org, world.Project, Period, Unique.Slug("race"));
        var pending = ledger.CaptureErrorAsync(row.Sql, row.Parameters);

        await world.WaitForBlockedByAsync(holderPid);
        pending.IsCompleted.ShouldBeFalse("the ledger insert waits for the statement's exclusive lock");

        await issue.CommitAsync();

        (await pending).ConstraintName.ShouldBe(
            Guard,
            "the insert re-reads the issued period once the lock is released");

        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}' and period = '{Period}'"))
            .ShouldBe(0, "the blocked insert must not slip a row into the issued period");
    }

    // ------------------------------------------------------------------ helpers

    private const string LedgerSql =
        """
        insert into public.usage_ledger_entry
            (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at)
        values (@id, @org_id, @project_id, 'results', 1, 'count', @period, @billing_key, now())
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
    private static (string Sql, (string Name, object Value)[] Parameters) LedgerRow(
        Guid org,
        Guid project,
        string period,
        string billingKey,
        Guid? id = null) =>
        (LedgerSql,
        [
            ("id", id ?? Guid.CreateVersion7()),
            ("org_id", org),
            ("project_id", project),
            ("period", period),
            ("billing_key", billingKey),
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

    /// <summary>Issues a period as the app role inside its tenant transaction: what the route does.</summary>
    private async Task IssueAsAppAsync(UsageQuotaWorld world, Guid org, string period)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        var statement = StatementRow(org, period);
        (await scope.ExecuteAsync(statement.Sql, statement.Parameters)).ShouldBe(1, $"{period} must be issuable");
        await scope.CommitAsync();
    }
}
