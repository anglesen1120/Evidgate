using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The role boundary of the queue mechanism after the worker split: producers keep
/// the app role with INSERT alone, the queue role owns the DML, and the tenant path
/// reads the two scoped views instead of the mechanism tables.
/// </summary>
[Collection(IsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class WorkerRoleGrantTests
{
    private readonly PostgresIsolationFixture _fixture;

    public WorkerRoleGrantTests(PostgresIsolationFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The app role may add work and may do nothing else with it: SELECT, UPDATE and
    /// DELETE answer 42501 on both mechanism tables, while the worker role reads them.
    /// </summary>
    [Fact]
    public async Task AppRole_CannotSelectUpdateOrDelete_OutboxAndTask_42501()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var worker = _fixture.WorkerConnectionString(database);
        var app = _fixture.AppConnectionString(database);

        await using (var workerConnection = new NpgsqlConnection(worker))
        {
            await workerConnection.OpenAsync(Token);
            await using var positive = workerConnection.CreateCommand();
            positive.CommandText = "select count(*)::int from public.task";
            (await positive.ExecuteScalarAsync(Token)).ShouldBe(0);
        }

        await using var scope = await TenantScope.BeginAsync(app, Guid.CreateVersion7());

        foreach (var statement in new[]
        {
            "select count(*)::int from public.task",
            "select count(*)::int from public.outbox",
            "update public.task set state = state where false",
            "update public.outbox set dispatched_at = dispatched_at where false",
            "delete from public.task where false",
            "delete from public.outbox where false",
        })
        {
            var refused = await scope.CaptureErrorAsync(statement);
            refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, statement);
        }
    }

    /// <summary>
    /// The producer half of the contract: both tables accept an app-role insert, and
    /// the row is committed rather than filtered by a tenant policy.
    /// </summary>
    [Fact]
    public async Task AppRole_CanInsert_OutboxAndTask()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var org = await SeedOrgAsync(database);
        var app = _fixture.AppConnectionString(database);

        await using (var scope = await TenantScope.BeginAsync(app, org))
        {
            (await scope.ExecuteAsync(
                """
                insert into public.task (id, org_id, queue, task_type, payload, idempotency_key)
                values (@id, @org, 'grants', 'test.record', '{}'::jsonb, @key)
                """,
                ("id", Guid.CreateVersion7()),
                ("org", org),
                ("key", "grant:task:" + Guid.NewGuid().ToString("N")))).ShouldBe(1);

            (await scope.ExecuteAsync(
                """
                insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key)
                values (@id, @org, 'record', @aggregate, 'test.record', '{}'::jsonb, @key)
                """,
                ("id", Guid.CreateVersion7()),
                ("org", org),
                ("aggregate", Guid.CreateVersion7()),
                ("key", "grant:outbox:" + Guid.NewGuid().ToString("N")))).ShouldBe(1);

            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database, $"select count(*) from public.task where org_id = '{org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            database, $"select count(*) from public.outbox where org_id = '{org:D}'")).ShouldBe(1);
    }

    /// <summary>
    /// The scoped views are the app role's only read of the mechanism: one tenant sees
    /// its own rows, a transaction without a tenant sees none, and the base tables stay
    /// unreadable.
    /// </summary>
    [Fact]
    public async Task AppRole_CanSelectScopedViews_OnlyOwnOrg()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var own = await SeedOrgAsync(database);
        var other = await SeedOrgAsync(database);
        await InsertTaskAsync(database, own, "view:own");
        await InsertTaskAsync(database, other, "view:other");
        await InsertOutboxAsync(database, own, "view:own");
        await InsertOutboxAsync(database, other, "view:other");

        var app = _fixture.AppConnectionString(database);

        await using (var scope = await TenantScope.BeginAsync(app, own))
        {
            (await scope.ScalarAsync<long>(
                "select count(*) from public.task_in_scope where idempotency_key = 'view:own'")).ShouldBe(1);
            (await scope.ScalarAsync<long>(
                "select count(*) from public.task_in_scope where idempotency_key = 'view:other'")).ShouldBe(0);
            (await scope.ScalarAsync<long>(
                "select count(*) from public.outbox_in_scope where idempotency_key = 'view:own'")).ShouldBe(1);
            (await scope.ScalarAsync<long>(
                "select count(*) from public.outbox_in_scope where idempotency_key = 'view:other'")).ShouldBe(0);
        }

        await using (var noTenant = await TenantScope.BeginAsync(app))
        {
            (await noTenant.ScalarAsync<long>("select count(*) from public.task_in_scope")).ShouldBe(0);
            (await noTenant.ScalarAsync<long>("select count(*) from public.outbox_in_scope")).ShouldBe(0);
        }

        // The worker role has no reason to read the tenant views, and the owner sees both rows.
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.task")).ShouldBe(2);
    }

    /// <summary>
    /// The queue role is a two-table role: outside task/outbox it holds no table,
    /// column, sequence or view privilege — only schema usage, which SQL needs.
    /// </summary>
    [Fact]
    public async Task WorkerRole_HasNoPrivilege_OnOtherTables()
    {
        var database = await _fixture.CreateDatabaseAsync();

        var tables = await _fixture.ColumnAsync<string>(
            database,
            """
            select n.nspname || '.' || c.relname
              from pg_class c
              join pg_namespace n on n.oid = c.relnamespace
             where n.nspname = 'public'
               and c.relkind in ('r', 'p', 'v', 'm', 'f')
            """);
        tables.Count.ShouldBeGreaterThan(20, "the catalogue must list the migrated schema");

        var privileged = new List<string>();
        foreach (var table in tables.Where(name => name is not ("public.task" or "public.outbox")))
        {
            if (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_table_privilege('evidgate_worker', '{table}', "
                + "'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')"))
            {
                privileged.Add(table);
            }
        }

        privileged.ShouldBeEmpty("evidgate_worker owns the queue tables and nothing else");

        var mechanism = await _fixture.ScalarAsync<bool>(
            database,
            "select has_table_privilege('evidgate_worker', 'public.task', 'SELECT,INSERT,UPDATE,DELETE')");
        mechanism.ShouldBeTrue("the positive control: the queue tables are the worker's own");

        var sequences = await _fixture.ColumnAsync<string>(
            database,
            """
            select c.relname
              from pg_class c
              join pg_namespace n on n.oid = c.relnamespace
             where n.nspname = 'public' and c.relkind = 'S'
               and has_sequence_privilege('evidgate_worker', c.oid, 'USAGE,SELECT,UPDATE')
            """);
        sequences.ShouldBeEmpty("the queue uses application-generated UUIDs, not sequences");
    }

    private Task<Guid> SeedOrgAsync(string database) => RunAsync(async connection =>
    {
        var org = Guid.CreateVersion7();
        await using var command = connection.CreateCommand();
        command.CommandText = "insert into public.organization (id, name, slug) values (@id, 'grants', @slug)";
        command.Parameters.AddWithValue("@id", org);
        command.Parameters.AddWithValue("@slug", "grants-" + org.ToString("N"));
        await command.ExecuteNonQueryAsync(Token);
        return org;
    }, _fixture.OwnerConnectionString(database));

    private Task<bool> InsertTaskAsync(string database, Guid org, string key) => RunAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into public.task (id, org_id, queue, task_type, payload, idempotency_key)
            values (@id, @org, 'grants', 'test.record', '{}'::jsonb, @key)
            """;
        command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@org", org);
        command.Parameters.AddWithValue("@key", key);
        await command.ExecuteNonQueryAsync(Token);
        return true;
    }, _fixture.WorkerConnectionString(database));

    private Task<bool> InsertOutboxAsync(string database, Guid org, string key) => RunAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key)
            values (@id, @org, 'record', @aggregate, 'test.record', '{}'::jsonb, @key)
            """;
        command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@org", org);
        command.Parameters.AddWithValue("@aggregate", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@key", key);
        await command.ExecuteNonQueryAsync(Token);
        return true;
    }, _fixture.WorkerConnectionString(database));

    private static async Task<T> RunAsync<T>(Func<NpgsqlConnection, Task<T>> work, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        return await work(connection);
    }
}
