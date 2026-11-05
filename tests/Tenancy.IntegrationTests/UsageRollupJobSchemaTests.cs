// The roll-up job's migration (0076) and the duplicate tally it introduces: the table's shape and grants, the sweep
// definer's ACL (the schedule role executes it, the app role cannot), the roll-up grant that forbids DELETE, and the
// duplicate attempts the ledger path counts in the caller's own transaction.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 0076's two surfaces. <c>usage_duplicate_attempt</c> is the only record of a duplicate billing key — the ledger answers
/// <c>duplicates</c> and writes nothing — so its grants are the app role's SELECT past the tenant policy, an INSERT and a
/// column-scoped UPDATE on the recount, never DELETE. The definer is the platform task's way to learn which orgs hold
/// usage: org ids only, and EXECUTE for the sweep role alone.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageRollupJobSchemaTests
{
    private const string Table = "usage_duplicate_attempt";

    private readonly PostgresTenancyFixture _fixture;

    public UsageRollupJobSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ the table

    [Fact]
    public async Task DuplicateAttempt_CarriesTheTenantShape_AndTheRecountGrants()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;

        (await _fixture.TableExistsAsync(database, Table)).ShouldBeTrue();
        var constraints = await _fixture.ConstraintNamesAsync(database, Table);
        constraints.ShouldContain("u:usage_duplicate_attempt_org_id_key");
        constraints.ShouldContain("u:usage_duplicate_attempt_billing_key_key");

        (await _fixture.RowSecurityAsync(database, Table)).ShouldBe((true, true));
        var policies = await _fixture.PoliciesAsync(database, Table);
        policies.ShouldContain(policy => policy.Contains("evidgate_app", StringComparison.Ordinal));
        policies.ShouldContain(policy => policy.Contains("evidgate_owner", StringComparison.Ordinal));

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "SELECT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "INSERT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "UPDATE"))
            .ShouldBeFalse("the recount is a column-scoped update");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "DELETE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", Table, "SELECT"))
            .ShouldBeFalse("the reconciliation read reaches the tally as the audited app role, never as the operator");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_worker_sched", Table, "SELECT")).ShouldBeFalse();

        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.usage_duplicate_attempt', 'attempts', 'UPDATE')"))
            .ShouldBeTrue("the tally increments `attempts`");
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.usage_duplicate_attempt', 'last_seen_at', 'UPDATE')"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.usage_duplicate_attempt', 'billing_key', 'UPDATE')"))
            .ShouldBeFalse("a duplicate row's key never moves");
    }

    [Fact]
    public async Task UsageRollup_RefusesTheAppRolesDelete()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var org = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org:D}', 'acme', 'acme-{org.ToString("N")[..8]}');
            insert into public.usage_rollup (id, org_id, project_id, period, metric, total_quantity, row_count, computed_at)
            values ('{Guid.CreateVersion7():D}', '{org:D}', null, '2026-02', 'results', 1, 1, now());
            """);

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "usage_rollup", "DELETE")).ShouldBeFalse();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
        var error = await scope.CaptureErrorAsync("delete from public.usage_rollup");
        error.SqlState.ShouldBe("42501", "a recompute updates an aggregate; it never removes one");

        // The refused statement aborted that transaction (25P02), so the surviving row is counted outside it.
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.usage_rollup")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the definer

    [Fact]
    public async Task OrgScanDefiner_IsOwnerOwned_Restricted_AndReturnsOrgIdsOnly()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org:D}', 'acme', 'acme-{org.ToString("N")[..8]}');
            insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'pp', 'pp-{org.ToString("N")[..8]}');
            insert into public.usage_ledger_entry
                (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at)
            values ('{Guid.CreateVersion7():D}', '{org:D}', '{project:D}', 'results', 2, 'batch', '2026-02',
                    'scan:{Guid.CreateVersion7():N}', now());
            """);

        var (definer, owner, config) = await FunctionAsync(database);
        definer.ShouldBeTrue("the platform task holds no tenant row of its own");
        owner.ShouldBe("evidgate_owner");
        config.ShouldContain("search_path=\"\"", Case.Sensitive);

        (await _fixture.ScalarAsync<bool>(database, ExecutePrivilegeSql("evidgate_worker_sched")))
            .ShouldBeTrue("the schedule is the caller the grant names");
        (await _fixture.ScalarAsync<bool>(database, ExecutePrivilegeSql("evidgate_app"))).ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(database, ExecutePrivilegeSql("evidgate_operator"))).ShouldBeFalse();

        // Org ids and nothing else: the projection cannot leak a row, a period or an amount of a tenant.
        (await _fixture.ScalarAsync<string>(
            database,
            "select pg_get_function_result('evidgate.usage_ledger_org_ids(uuid, integer)'::regprocedure)"))
            .ShouldBe("SETOF uuid");
        var page = await _fixture.ColumnAsync<Guid>(
            database,
            "select * from evidgate.usage_ledger_org_ids(null, 10)");
        page.ShouldContain(org, "the function returns the tenant that holds a ledger row");

        // The keyset bound: after a page that ends on the org, the next page is empty.
        (await _fixture.ColumnAsync<Guid>(
            database,
            $"select * from evidgate.usage_ledger_org_ids('{org:D}', 10)")).ShouldBeEmpty();

        // The app role is refused before the body runs.
        await using var app = await OpenAsync(_fixture.AppConnectionString(database));
        await using var call = app.CreateCommand();
        call.CommandText = "select * from evidgate.usage_ledger_org_ids(null, 10)";
        var denied = await Should.ThrowAsync<PostgresException>(() => call.ExecuteReaderAsync(TestContext.Current.CancellationToken));
        denied.SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ the tally in the ledger path

    [Fact]
    public async Task ReplayedKey_CountsOneAttemptPerReplay_AndAnInBatchRepeatCountsToo()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var world = await StartWorldAsync(database);

        var first = await RecordAsync(world, ["batch:a:results", "batch:b:results"]);
        first.Inserted.Count.ShouldBe(2);
        first.Duplicates.ShouldBeEmpty();
        (await AttemptsAsync(database)).ShouldBeEmpty("a fresh batch has no duplicate to count");

        // The same source batch arrives again: both keys are duplicates, and each attempt is now on file.
        var resent = await RecordAsync(world, ["batch:a:results", "batch:b:results"]);
        resent.Duplicates.ShouldBe(["batch:a:results", "batch:b:results"]);
        (await AttemptsAsync(database)).ShouldBe(["batch:a:results|1", "batch:b:results|1"]);

        await RecordAsync(world, ["batch:a:results", "batch:b:results"]);
        (await AttemptsAsync(database)).ShouldBe(["batch:a:results|2", "batch:b:results|2"]);

        // A key the request itself repeats is a duplicate even though its first occurrence inserted: the tally records
        // the attempt, so the reconciliation can report a duplicate against a key that exists once.
        var repeated = await RecordAsync(world, ["batch:c:results", "batch:c:results"]);
        repeated.Inserted.ShouldBe(["batch:c:results"]);
        repeated.Duplicates.ShouldBe(["batch:c:results"]);
        (await AttemptsAsync(database)).ShouldBe(["batch:a:results|2", "batch:b:results|2", "batch:c:results|1"]);
    }

    [Fact]
    public async Task RolledBackBatch_LeavesNeitherALedgerRowNorAnAttempt()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var world = await StartWorldAsync(database);

        await RecordAsync(world, ["batch:keep:results"]);
        // A refused unit of work: the scope is disposed uncommitted, as a caller's exception does.
        await RecordAndRollBackAsync(world, ["batch:keep:results", "batch:lost:results"]);

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}'"))
            .ShouldBe(1, "the rolled-back insert wrote no row");
        (await AttemptsAsync(database)).ShouldBeEmpty(
            "the attempt was counted in the caller's transaction, so the rollback removed it too");

        // The same call committed: the duplicate is counted and the new key inserted.
        var committed = await RecordAsync(world, ["batch:keep:results", "batch:lost:results"]);
        committed.Duplicates.ShouldBe(["batch:keep:results"]);
        (await AttemptsAsync(database)).ShouldBe(["batch:keep:results|1"]);
    }

    private sealed record World(Guid Org, Guid Project, TenancyApiFactory Host) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }

    private async Task<World> StartWorldAsync(string database)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var suffix = org.ToString("N")[..8];
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org:D}', 'acme', 'acme-{suffix}');
            insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'pp', 'pp-{suffix}');
            """);
        return new World(org, project, TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database)));
    }

    private static UsageEntryInput Entry(string billingKey, World world) =>
        new(billingKey, UsageMetric.Results, world.Project, 1m, "batch", "2026-02", DateTimeOffset.UtcNow);

    private static async Task<UsageBatchResult> RecordAsync(World world, IReadOnlyList<string> keys)
    {
        await using var services = world.Host.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var result = await provider.GetRequiredService<IUsageLedger>().RecordAsync(
            new RecordUsageBatchRequest([.. keys.Select(key => Entry(key, world))]),
            TestContext.Current.CancellationToken);
        await scope.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private static async Task RecordAndRollBackAsync(World world, IReadOnlyList<string> keys)
    {
        await using var services = world.Host.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        await provider.GetRequiredService<IUsageLedger>().RecordAsync(
            new RecordUsageBatchRequest([.. keys.Select(key => Entry(key, world))]),
            TestContext.Current.CancellationToken);
        // No CommitAsync: disposal rolls the transaction back, as a failing caller's unit of work does.
    }

    /// <summary>The tally rows of the world's org as <c>billing_key|attempts</c>, in key order.</summary>
    private Task<IReadOnlyList<string>> AttemptsAsync(string database) =>
        _fixture.ColumnAsync<string>(
            database,
            "select billing_key || '|' || attempts::text from public.usage_duplicate_attempt order by billing_key");

    private async Task<(bool Definer, string Owner, string Config)> FunctionAsync(string database)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select p.prosecdef, pg_catalog.pg_get_userbyid(p.proowner), coalesce(array_to_string(p.proconfig, ','), '')
            from pg_catalog.pg_proc p
            where p.oid = 'evidgate.usage_ledger_org_ids(uuid, integer)'::regprocedure
            """;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue("the definer of 0076 exists");
        return (reader.GetBoolean(0), reader.GetString(1), reader.GetString(2));
    }

    private static string ExecutePrivilegeSql(string role) =>
        $"select has_function_privilege('{role}', 'evidgate.usage_ledger_org_ids(uuid, integer)', 'EXECUTE')";

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}
