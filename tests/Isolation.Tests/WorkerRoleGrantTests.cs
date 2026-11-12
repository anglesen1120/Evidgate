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
            // A refused statement aborts its transaction (25P02), so every denial gets
            // its own: sharing one transaction would read the previous failure's corpse
            // instead of this statement's answer, and only the first line would be proven.
            await using var scope = await TenantScope.BeginAsync(app, Guid.CreateVersion7());
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

        // The privilege test is a function, so it cannot share a WHERE with relkind = 'S': the
        // planner pushes it into the pg_class scan ABOVE the relkind comparison, and the first
        // TOAST relation it meets answers 42809 ("pg_toast_…" is not a sequence) instead of being
        // skipped. WorkerSequencesAsync materializes the sequence set first, so the argument is
        // always a sequence OID.
        (await WorkerSequencesAsync(database)).ShouldBeEmpty(
            "the queue uses application-generated UUIDs, not sequences");

        // The positive control: a sequence the worker holds is reported, and the revoke takes it
        // away again — an empty answer above cannot be the query's own blindness.
        await _fixture.ExecuteAsync(database, "create sequence public.worker_role_probe_seq");
        await _fixture.ExecuteAsync(
            database,
            "grant usage, select, update on sequence public.worker_role_probe_seq to evidgate_worker");
        (await WorkerSequencesAsync(database)).ShouldBe(
            ["worker_role_probe_seq"],
            "a sequence the worker holds must be reported by the same query");
        await _fixture.ExecuteAsync(
            database,
            "revoke all on sequence public.worker_role_probe_seq from evidgate_worker");
        (await WorkerSequencesAsync(database)).ShouldBeEmpty("the revoke removes it again");
    }

    /// <summary>
    /// The app role's privilege on the mechanism is the producer's INSERT and nothing else:
    /// the verbs it lost stay lost, TRUNCATE/REFERENCES/TRIGGER were never granted, and no
    /// column-level grant hides a second path in the ACL.
    /// </summary>
    [Fact]
    public async Task AppRole_Privileges_AreInsertAlone_OnQueueTables()
    {
        var database = await _fixture.CreateDatabaseAsync();

        foreach (var table in new[] { "public.task", "public.outbox" })
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_table_privilege('evidgate_app', '{table}', 'INSERT')"))
                .ShouldBeTrue($"{table}: the producer keeps INSERT");

            foreach (var privilege in new[] { "SELECT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
            {
                (await _fixture.ScalarAsync<bool>(
                    database,
                    $"select has_table_privilege('evidgate_app', '{table}', '{privilege}')"))
                    .ShouldBeFalse($"{table}: evidgate_app must hold no {privilege}");
            }

            (await ColumnGrantsAsync(database, table, "evidgate_app")).ShouldBeEmpty(
                $"{table}: a column grant would be a second path the table-level scan cannot see");

            // The effective probe, which also sees a grant inherited through PUBLIC: every
            // column answers for INSERT (the table grant) and for nothing else.
            (await _fixture.ColumnAsync<string>(
                database,
                $"""
                select a.attname || ':' || p
                  from pg_catalog.pg_attribute a
                  cross join unnest(array['SELECT', 'UPDATE', 'REFERENCES']) p
                 where a.attrelid = '{table}'::regclass
                   and a.attnum > 0 and not a.attisdropped
                   and pg_catalog.has_column_privilege('evidgate_app', a.attrelid, a.attname, p)
                """)).ShouldBeEmpty($"{table}: no effective column privilege beyond the producer's INSERT");

            (await _fixture.ScalarAsync<bool>(
                database,
                $"""
                select not exists (
                  select 1 from pg_catalog.pg_attribute a
                   where a.attrelid = '{table}'::regclass
                     and a.attnum > 0 and not a.attisdropped
                     and not pg_catalog.has_column_privilege('evidgate_app', a.attrelid, a.attname, 'INSERT'))
                """)).ShouldBeTrue($"{table}: the positive control — INSERT is effective on every column");
        }
    }

    /// <summary>
    /// The queue role's only column-scoped privilege is the dispatch stamp: the ACL names
    /// <c>dispatched_at</c> and no other column of either table.
    /// </summary>
    [Fact]
    public async Task WorkerRole_ColumnGrants_AreTheDispatchStampAlone()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await ColumnGrantsAsync(database, "public.outbox", "evidgate_worker"))
            .ShouldBe(["dispatched_at:UPDATE"], "the positive control: a column grant is visible here");
        (await ColumnGrantsAsync(database, "public.task", "evidgate_worker")).ShouldBeEmpty(
            "the task table's UPDATE is table-wide, and nothing else is column-scoped");
    }

    /// <summary>
    /// Neither the operator role nor PUBLIC holds a privilege on the mechanism: the audited
    /// operator page is the definer, not a standing grant.
    /// </summary>
    [Fact]
    public async Task OperatorAndPublic_HoldNothing_OnQueueTables()
    {
        var database = await _fixture.CreateDatabaseAsync();

        var appInserts = await _fixture.ScalarAsync<long>(
            database,
            """
            select count(*) from information_schema.table_privileges
             where table_schema = 'public' and table_name in ('task', 'outbox')
               and grantee = 'evidgate_app' and privilege_type = 'INSERT'
            """);
        appInserts.ShouldBe(2, "the positive control: the catalogue reports the grants that exist");

        var granted = await _fixture.ColumnAsync<string>(
            database,
            """
            select grantee || ':' || privilege_type || ':' || table_name
              from information_schema.table_privileges
             where table_schema = 'public' and table_name in ('task', 'outbox')
               and grantee in ('PUBLIC', 'evidgate_operator')
            """);

        granted.ShouldBeEmpty("no standing grant reaches the operator role or PUBLIC");

        // The table-level catalogue carries no column grant at all, so the ACL is read directly,
        // with PUBLIC matched by OID 0 (it has no pg_roles row): a SELECT(payload) granted there
        // would otherwise reach every role unseen.
        foreach (var grantee in new[] { "PUBLIC", "evidgate_operator" })
        {
            foreach (var table in new[] { "public.task", "public.outbox" })
            {
                (await ColumnGrantsAsync(database, table, grantee)).ShouldBeEmpty(
                    $"{table}: no column grant reaches {grantee}");
            }
        }

        // A PUBLIC grant is effective for every role, so a role that holds nothing else answers
        // for it: the sweep role can reach no column of the task table.
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_worker_sched', 'public.task', 'payload', 'SELECT')"))
            .ShouldBeFalse("a PUBLIC column grant on the mechanism would be effective here");
    }

    /// <summary>
    /// The queue role reaches no function: the two definers are the app role's capability and
    /// no ACL in the schema names the worker.
    /// </summary>
    [Fact]
    public async Task WorkerRole_HoldsNoExecute()
    {
        var database = await _fixture.CreateDatabaseAsync();

        var definers = new[]
        {
            "evidgate.list_dead_letter_tasks(text, timestamp with time zone, uuid, integer)",
            "evidgate.replay_dead_letter_task(uuid, uuid)",
        };

        foreach (var definer in definers)
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_function_privilege('evidgate_worker', '{definer}', 'EXECUTE')"))
                .ShouldBeFalse(definer);

            // The positive control: the same probe sees the app role's grant.
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_function_privilege('evidgate_app', '{definer}', 'EXECUTE')"))
                .ShouldBeTrue(definer);
        }

        var granted = await _fixture.ColumnAsync<string>(
            database,
            """
            select p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')'
              from pg_proc p
              join pg_namespace n on n.oid = p.pronamespace
              cross join lateral pg_catalog.aclexplode(p.proacl) acl
              join pg_roles r on r.oid = acl.grantee
             where n.nspname = 'evidgate' and r.rolname = 'evidgate_worker'
            """);

        granted.ShouldBeEmpty("no evidgate function names the worker in its ACL");
    }

    /// <summary>
    /// The <c>public</c> sequences on which the worker role holds any sequence privilege. The
    /// sequence set is MATERIALIZED before <c>has_sequence_privilege</c> runs: without the barrier
    /// the planner attaches the function to the <c>pg_class</c> scan ahead of the <c>relkind</c>
    /// test and raises 42809 on the first TOAST relation (PostgreSQL 18.6).
    /// </summary>
    private Task<IReadOnlyList<string>> WorkerSequencesAsync(string database) =>
        _fixture.ColumnAsync<string>(
            database,
            """
            with sequences as materialized (
                select c.relname, c.oid
                  from pg_class c
                  join pg_namespace n on n.oid = c.relnamespace
                 where n.nspname = 'public' and c.relkind = 'S'
            )
            select relname
              from sequences
             where has_sequence_privilege('evidgate_worker', oid, 'USAGE,SELECT,UPDATE')
            """);

    /// <summary>
    /// Column-level grants the ACL records for one grantee on one table, as <c>column:privilege</c>.
    /// The grantee is matched through a left join, because PUBLIC is not a row in <c>pg_roles</c>
    /// (OID 0): an inner join would silently hide a grant that reaches every role.
    /// </summary>
    private Task<IReadOnlyList<string>> ColumnGrantsAsync(string database, string table, string grantee) =>
        _fixture.ColumnAsync<string>(
            database,
            $"""
            select a.attname || ':' || acl.privilege_type
              from pg_catalog.pg_attribute a
              cross join lateral pg_catalog.aclexplode(a.attacl) acl
              left join pg_catalog.pg_roles r on r.oid = acl.grantee
             where a.attrelid = '{table}'::regclass
               and a.attnum > 0 and not a.attisdropped
               and coalesce(r.rolname, case when acl.grantee = 0 then 'PUBLIC' end) = '{grantee}'
            """);

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

    /// <summary>
    /// One outbox row seeded as the owner: the queue role reads and stamps that table but
    /// does not produce work (only <c>evidgate_app</c> holds its INSERT).
    /// </summary>
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
    }, _fixture.OwnerConnectionString(database));

    private static async Task<T> RunAsync<T>(Func<NpgsqlConnection, Task<T>> work, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        return await work(connection);
    }
}
