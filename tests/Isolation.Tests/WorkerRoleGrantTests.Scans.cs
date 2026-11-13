using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>The worker role's outbox refusals and the catalogue scans shared with the grant tests.</summary>
public sealed partial class WorkerRoleGrantTests
{
    /// <summary>
    /// The queue role cannot produce or purge outbox rows: INSERT and DELETE answer 42501 on the
    /// statement, and it holds no TRUNCATE, REFERENCES or TRIGGER on any table of any non-system
    /// schema (the queue tables included).
    /// </summary>
    [Fact]
    public async Task WorkerRole_CannotInsertOrDeleteOutbox_AndHoldsNoTruncateReferencesTrigger()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var org = await SeedOrgAsync(database);
        await InsertOutboxAsync(database, org, "worker:refuse");

        var worker = _fixture.WorkerConnectionString(database);
        foreach (var statement in new[]
        {
            "insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key) "
            + "values (gen_random_uuid(), gen_random_uuid(), 'record', gen_random_uuid(), 'test.record', '{}'::jsonb, 'worker:insert')",
            "delete from public.outbox",
        })
        {
            await using var connection = new NpgsqlConnection(worker);
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            var refused = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
            refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, statement);
        }

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.outbox"))
            .ShouldBe(1, "the refused delete left the row in place");

        // The positive control: the stamp the worker may write is accepted by the same connection.
        await using (var connection = new NpgsqlConnection(worker))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "update public.outbox set dispatched_at = now()";
            (await command.ExecuteNonQueryAsync(Token)).ShouldBe(1);
        }

        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_table_privilege('evidgate_worker', 'public.outbox', 'INSERT,DELETE')"))
            .ShouldBeFalse("the effective probe agrees with the statements");

        (await WorkerDdlPrivilegedRelationsAsync(database))
            .ShouldBeEmpty("evidgate_worker holds no TRUNCATE, REFERENCES or TRIGGER anywhere");

        // The positive control: the same query reports a grant on the queue table when one exists.
        await _fixture.ExecuteAsync(database, "grant references on public.task to evidgate_worker");
        (await WorkerDdlPrivilegedRelationsAsync(database)).ShouldBe(["public.task"]);

        // The other two verbs and a non-table relkind (a view) are visible to the same query.
        await _fixture.ExecuteAsync(database, "revoke references on public.task from evidgate_worker");
        await _fixture.ExecuteAsync(database, "grant truncate, trigger on public.outbox to evidgate_worker");
        await _fixture.ExecuteAsync(database, "create view public.worker_role_probe_view as select 1 as x");
        await _fixture.ExecuteAsync(database, "grant trigger on public.worker_role_probe_view to evidgate_worker");
        (await WorkerDdlPrivilegedRelationsAsync(database)).ShouldBe(
            ["public.outbox", "public.worker_role_probe_view"]);
    }

    private Task<IReadOnlyList<string>> WorkerDdlPrivilegedRelationsAsync(string database) =>
        _fixture.ColumnAsync<string>(
            database,
            $"""
            with relations as materialized (
                select n.nspname || '.' || c.relname as name, c.oid
                  from pg_class c
                  join pg_namespace n on n.oid = c.relnamespace
                 where {NonSystemSchema} and c.relkind in ('r', 'p', 'v', 'm', 'f')
            )
            select name from relations
             where has_table_privilege('evidgate_worker', oid, 'TRUNCATE,REFERENCES,TRIGGER')
             order by name
            """);

    /// <summary>
    /// Functions in any non-system schema the worker role can EXECUTE through any path (its own
    /// ACL, PUBLIC or the default ACL), minus extension members (their ACLs belong to the extension). A PUBLIC EXECUTE found here is revoked, never allow-listed.
    /// </summary>
    private Task<IReadOnlyList<string>> WorkerExecutableFunctionsAsync(string database) =>
        _fixture.ColumnAsync<string>(
            database,
            $"""
            with functions as materialized (
                select n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')' as name,
                       p.oid
                  from pg_proc p
                  join pg_namespace n on n.oid = p.pronamespace
                 where {NonSystemSchema}
                   and not exists (
                       select 1 from pg_depend d
                        where d.classid = 'pg_proc'::regclass and d.objid = p.oid and d.deptype = 'e')
            )
            select name
              from functions
             where has_function_privilege('evidgate_worker', oid, 'EXECUTE')
             order by name
            """);

    /// <summary>
    /// The sequences, in any non-system schema, on which the worker role holds any sequence
    /// privilege (directly or through PUBLIC), as <c>schema.name</c>. The sequence set is
    /// MATERIALIZED before <c>has_sequence_privilege</c> runs: without the barrier the planner
    /// attaches the function to the <c>pg_class</c> scan ahead of the <c>relkind</c> test and
    /// raises 42809 on the first TOAST relation (PostgreSQL 18.6).
    /// </summary>
    private Task<IReadOnlyList<string>> WorkerSequencesAsync(string database) =>
        _fixture.ColumnAsync<string>(
            database,
            $"""
            with sequences as materialized (
                select n.nspname || '.' || c.relname as name, c.oid
                  from pg_class c
                  join pg_namespace n on n.oid = c.relnamespace
                 where {NonSystemSchema} and c.relkind = 'S'
            )
            select name
              from sequences
             where has_sequence_privilege('evidgate_worker', oid, 'USAGE,SELECT,UPDATE')
             order by name
            """);

    private const string NonSystemSchema =
        "n.nspname not in ('pg_catalog', 'information_schema') "
        + "and n.nspname not like 'pg\\_toast%' and n.nspname not like 'pg\\_temp%'";
}
