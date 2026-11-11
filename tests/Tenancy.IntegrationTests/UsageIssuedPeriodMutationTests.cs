using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The UPDATE/DELETE guards of an issued billing period against a real PostgreSQL. The app role holds neither
/// privilege, so the two cases that speak as it first grant them in a private database (a grant would leak into
/// the run's shared one): the refusal under test is then the trigger, not the ACL. The owner needs no grant.
/// Positive controls (an open period, another tenant) sit next to every refusal so a refusal cannot be a missing
/// row or a broken statement.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class UsageIssuedPeriodMutationTests
{
    private const string MigrationFile = "0092_billing_issued_period_mutation_guard.sql";
    private const string LedgerGuard = "usage_ledger_entry_issued_mutation";
    private const string StatementGuard = "billing_statement_issued_mutation";
    private const string Issued = "2026-09";
    private const string Open = "2026-10";

    private readonly PostgresTenancyFixture _fixture;

    public UsageIssuedPeriodMutationTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Schema_GuardsJournalledAttachedAndOwnerOnly()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.ReadJournalAsync(database))
            .ShouldContain(row => row.Name == MigrationFile, "the real runner must journal the guards");

        foreach (var (table, trigger) in new[] { ("usage_ledger_entry", LedgerGuard), ("billing_statement", StatementGuard) })
        {
            (await _fixture.ScalarAsync<long>(
                database,
                "select count(*) from pg_catalog.pg_trigger "
                + $"where tgrelid = 'public.{table}'::regclass and tgname = '{trigger}' and not tgisinternal"
                + " and (tgtype & 16) = 16 and (tgtype & 8) = 8 and (tgtype & 1) = 1"))
                .ShouldBe(1, $"{table} carries one {trigger} row trigger on UPDATE and DELETE");
        }

        foreach (var function in new[]
        {
            "evidgate.usage_ledger_entry_issued_mutation_guard()",
            "evidgate.billing_statement_issued_mutation_guard()",
        })
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_function_privilege('evidgate_app', '{function}', 'EXECUTE')"))
                .ShouldBeFalse($"{function} is called by its trigger only");
        }
    }

    [Fact]
    public async Task Ledger_OwnerUpdateAndDelete_RefusedWhenIssued_AllowedWhenOpen()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var database = world.Database;
        var (issuedRow, openRow) = await SeedIssuedAndOpenAsync(world, OwnerString(database));

        await using (var owner = await TenantScope.BeginAsync(OwnerString(database), world.Org))
        {
            (await owner.CaptureErrorAsync($"update public.usage_ledger_entry set quantity = 9 where id = '{issuedRow}'"))
                .ConstraintName.ShouldBe(LedgerGuard);
        }

        await using (var owner = await TenantScope.BeginAsync(OwnerString(database), world.Org))
        {
            var error = await owner.CaptureErrorAsync($"delete from public.usage_ledger_entry where id = '{issuedRow}'");
            error.SqlState.ShouldBe("23514");
            error.ConstraintName.ShouldBe(LedgerGuard);
        }

        await using (var owner = await TenantScope.BeginAsync(OwnerString(database), world.Org))
        {
            (await owner.ExecuteAsync($"update public.usage_ledger_entry set quantity = 9 where id = '{openRow}'")).ShouldBe(1);
            (await owner.ExecuteAsync($"delete from public.usage_ledger_entry where id = '{openRow}'")).ShouldBe(1);
            await owner.CommitAsync();
        }

        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where id = '{issuedRow}' and quantity = 1 and version = 1"))
            .ShouldBe(1, "the refused statements leave the issued row byte-identical");
    }

    [Fact]
    public async Task Ledger_AppRoleUpdateAndDelete_RefusedByTriggerWhenIssued_AllowedWhenOpen()
    {
        // Dedicated: the UPDATE/DELETE grant it needs would leak into the shared database.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, database: database);
        var (issuedRow, openRow) = await SeedIssuedAndOpenAsync(world, OwnerString(database));
        await _fixture.ExecuteAsync(
            database, "grant update, delete on public.usage_ledger_entry, public.billing_statement to evidgate_app");

        await using (var app = await world.BeginAppScopeAsync())
        {
            (await app.CaptureErrorAsync($"update public.usage_ledger_entry set quantity = 9 where id = '{issuedRow}'"))
                .ConstraintName.ShouldBe(LedgerGuard);
        }

        await using (var app = await world.BeginAppScopeAsync())
        {
            (await app.CaptureErrorAsync($"delete from public.usage_ledger_entry where id = '{issuedRow}'"))
                .ConstraintName.ShouldBe(LedgerGuard);
        }

        await using (var app = await world.BeginAppScopeAsync())
        {
            (await app.ExecuteAsync($"update public.usage_ledger_entry set quantity = 9 where id = '{openRow}'")).ShouldBe(1);
            (await app.ExecuteAsync($"delete from public.usage_ledger_entry where id = '{openRow}'")).ShouldBe(1);
            await app.CommitAsync();
        }
    }

    [Fact]
    public async Task Ledger_MoveIntoIssuedPeriod_Refused_MoveBetweenOpenPeriodsAllowed()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var database = world.Database;
        var (_, openRow) = await SeedIssuedAndOpenAsync(world, OwnerString(database));

        await using (var owner = await TenantScope.BeginAsync(OwnerString(database), world.Org))
        {
            var error = await owner.CaptureErrorAsync(
                $"update public.usage_ledger_entry set period = '{Issued}' where id = '{openRow}'");
            error.ConstraintName.ShouldBe(LedgerGuard, "a row cannot be re-dated into an issued period");
            error.MessageText.ShouldContain(Issued);
        }

        await using (var owner = await TenantScope.BeginAsync(OwnerString(database), world.Org))
        {
            (await owner.ExecuteAsync($"update public.usage_ledger_entry set period = '2026-11' where id = '{openRow}'"))
                .ShouldBe(1, "a move between open periods stays allowed");
            await owner.CommitAsync();
        }
    }

    [Fact]
    public async Task Ledger_OtherTenantsIssuedPeriod_DoesNotGuardThisTenant()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        var owner = OwnerString(world.Database);
        var id = Guid.CreateVersion7();

        await InsertLedgerAsync(owner, otherOrg, otherProject, Issued, id);
        await InsertStatementAsync(owner, world.Org, Issued);

        await using var scope = await TenantScope.BeginAsync(owner, otherOrg);
        (await scope.ExecuteAsync($"update public.usage_ledger_entry set quantity = 5 where id = '{id}'"))
            .ShouldBe(1, "only the statement's own tenant is guarded");
        await scope.CommitAsync();
    }

    [Fact]
    public async Task Statement_UpdateAndDelete_RefusedForOwnerAndAppRole()
    {
        // Dedicated: the UPDATE/DELETE grant it needs would leak into the shared database.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, database: database);
        var owner = OwnerString(database);
        var id = await InsertStatementAsync(owner, world.Org, Issued);
        await _fixture.ExecuteAsync(
            database, "grant update, delete on public.usage_ledger_entry, public.billing_statement to evidgate_app");

        foreach (var sql in new[]
        {
            $"update public.billing_statement set total_amount = 99 where id = '{id}'",
            $"delete from public.billing_statement where id = '{id}'",
        })
        {
            await using (var asOwner = await TenantScope.BeginAsync(owner, world.Org))
            {
                var error = await asOwner.CaptureErrorAsync(sql);
                error.SqlState.ShouldBe("23514");
                error.ConstraintName.ShouldBe(StatementGuard);
            }

            await using var asApp = await world.BeginAppScopeAsync();
            (await asApp.CaptureErrorAsync(sql)).ConstraintName.ShouldBe(StatementGuard);
        }

        (await world.ScalarAsync<long>(
            $"select count(*) from public.billing_statement where id = '{id}' and total_amount = 0 and version = 1"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task StatementReplayInsert_StillPasses()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var database = world.Database;
        await SeedIssuedAndOpenAsync(world, OwnerString(database));

        // The issue flow's idempotent insert: a second issue of the same period collides and writes nothing, and the
        // guards never see an UPDATE or DELETE.
        await using var app = await world.BeginAppScopeAsync();
        (await app.ExecuteAsync(
            """
            insert into public.billing_statement
                (id, org_id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json,
                 issued_by, issued_at)
            values (gen_random_uuid(), @org, @period, 'invoice', 0, 'USD', 0, repeat('a', 64), '{}'::jsonb,
                    gen_random_uuid(), now())
            on conflict (org_id, period, kind) do nothing
            """,
            ("org", world.Org),
            ("period", Issued))).ShouldBe(0, "the replay inserts nothing and raises nothing");
        await app.CommitAsync();
    }

    [Fact]
    public async Task Ledger_IssuedRowMovedOut_OrMovedToIssuedTenant_Refused_MixedBulkRefusedWhole()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var database = world.Database;
        var (otherOrg, _) = await world.AddOtherOrgAsync();
        var (issuedRow, openRow) = await SeedIssuedAndOpenAsync(world, OwnerString(database));
        await InsertStatementAsync(OwnerString(database), otherOrg, Open);

        foreach (var sql in new[]
        {
            $"update public.usage_ledger_entry set period = '2026-12' where id = '{issuedRow}'",
            $"update public.usage_ledger_entry set org_id = '{otherOrg:D}', period = '{Open}' where id = '{openRow}'",
        })
        {
            await using var scope = await TenantScope.BeginAsync(OwnerString(database), world.Org);
            (await scope.CaptureErrorAsync(sql)).ConstraintName.ShouldBe(LedgerGuard, sql);
        }

        // One statement over an issued and an open row fails whole: the open row keeps its quantity.
        await using (var scope = await TenantScope.BeginAsync(OwnerString(database), world.Org))
        {
            (await scope.CaptureErrorAsync(
                $"update public.usage_ledger_entry set quantity = 7 where org_id = '{world.Org:D}'"))
                .ConstraintName.ShouldBe(LedgerGuard);
        }

        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}' and quantity = 1"))
            .ShouldBe(2, "a refused bulk statement changes no row");
    }

    [Fact]
    public async Task Ledger_UpdateOrDeleteUnderRepeatableRead_Refused()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var database = world.Database;
        var (_, openRow) = await SeedIssuedAndOpenAsync(world, OwnerString(database));

        foreach (var sql in new[]
        {
            $"update public.usage_ledger_entry set quantity = 9 where id = '{openRow}'",
            $"delete from public.usage_ledger_entry where id = '{openRow}'",
        })
        {
            await using var connection = new NpgsqlConnection(OwnerString(database));
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead, TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            var error = await Should.ThrowAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            error.SqlState.ShouldBe("25000", sql);
        }
    }

    [Fact]
    public async Task Issue_WhileLedgerUpdateIsInFlight_WaitsForIt()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var database = world.Database;
        var (_, openRow) = await SeedIssuedAndOpenAsync(world, OwnerString(database));

        await using var mover = await TenantScope.BeginAsync(OwnerString(database), world.Org);
        var holderPid = await mover.ScalarAsync<int>("select pg_backend_pid()");
        (await mover.ExecuteAsync($"update public.usage_ledger_entry set quantity = 2 where id = '{openRow}'"))
            .ShouldBe(1);

        await using var issue = await TenantScope.BeginAsync(OwnerString(database), world.Org);
        var pending = issue.ExecuteAsync(
            """
            insert into public.billing_statement
                (id, org_id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json,
                 issued_by, issued_at)
            values (gen_random_uuid(), @org, @period, 'invoice', 0, 'USD', 0, repeat('a', 64), '{}'::jsonb,
                    gen_random_uuid(), now())
            """,
            ("org", world.Org),
            ("period", Open));

        await world.WaitForBlockedByAsync(holderPid);
        pending.IsCompleted.ShouldBeFalse("the issue waits for the in-flight ledger update's shared lock");

        await mover.CommitAsync();
        (await pending).ShouldBe(1);
        await issue.CommitAsync();
    }

    [Fact]
    public async Task Mutation_DroppingTheTriggers_LetsIssuedPeriodBeChanged()
    {
        // Dedicated: dropping the triggers is schema DDL.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, database: database);
        var (issuedRow, _) = await SeedIssuedAndOpenAsync(world, OwnerString(database));
        var statementId = await world.ScalarAsync<Guid>(
            $"select id from public.billing_statement where org_id = '{world.Org:D}' and period = '{Issued}'");

        await _fixture.ExecuteAsync(
            database,
            $"""
            drop trigger {LedgerGuard} on public.usage_ledger_entry;
            drop trigger {StatementGuard} on public.billing_statement;
            """);

        await using var owner = await TenantScope.BeginAsync(OwnerString(database), world.Org);
        (await owner.ExecuteAsync($"update public.usage_ledger_entry set quantity = 9 where id = '{issuedRow}'"))
            .ShouldBe(1, "without the trigger the tests above would fail");
        (await owner.ExecuteAsync($"delete from public.billing_statement where id = '{statementId}'")).ShouldBe(1);
        await owner.CommitAsync();
    }

    [Fact]
    public async Task Migration_Reapplied_LeavesOneTriggerEach()
    {
        // Dedicated: it deletes the journal row and re-applies the migration.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await _fixture.ExecuteAsync(database, $"delete from public.schema_migration where name = '{MigrationFile}'");

        var again = _fixture.ApplyMigrations(database);

        again.ExitCode.ShouldBe(MigratorExitCodes.Success, again.Transcript);
        again.Result.AppliedNow.ShouldBe([MigrationFile]);
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_catalog.pg_trigger where tgname in "
            + $"('{LedgerGuard}', '{StatementGuard}') and not tgisinternal"))
            .ShouldBe(2);
    }

    // ------------------------------------------------------------------ helpers

    private string OwnerString(string database) => _fixture.OwnerConnectionString(database);

    /// <summary>One ledger row in the issued period (written before its statement) and one in an open period.</summary>
    private static async Task<(Guid IssuedRow, Guid OpenRow)> SeedIssuedAndOpenAsync(UsageQuotaWorld world, string owner)
    {
        var issuedRow = Guid.CreateVersion7();
        var openRow = Guid.CreateVersion7();
        await InsertLedgerAsync(owner, world.Org, world.Project, Issued, issuedRow);
        await InsertLedgerAsync(owner, world.Org, world.Project, Open, openRow);
        await InsertStatementAsync(owner, world.Org, Issued);

        return (issuedRow, openRow);
    }

    private static async Task InsertLedgerAsync(string owner, Guid org, Guid project, string period, Guid id)
    {
        await using var scope = await TenantScope.BeginAsync(owner, org);
        (await scope.ExecuteAsync(
            """
            insert into public.usage_ledger_entry
                (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at)
            values (@id, @org, @project, 'results', 1, 'count', @period, @key, now())
            """,
            ("id", id),
            ("org", org),
            ("project", project),
            ("period", period),
            ("key", Unique.Slug("k")))).ShouldBe(1);
        await scope.CommitAsync();
    }

    private static async Task<Guid> InsertStatementAsync(string owner, Guid org, string period)
    {
        var id = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(owner, org);
        (await scope.ExecuteAsync(
            """
            insert into public.billing_statement
                (id, org_id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json,
                 issued_by, issued_at)
            values (@id, @org, @period, 'invoice', 0, 'USD', 0, repeat('a', 64), '{}'::jsonb, @by, now())
            """,
            ("id", id),
            ("org", org),
            ("period", period),
            ("by", Guid.CreateVersion7()))).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }
}
