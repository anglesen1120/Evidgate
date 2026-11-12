using System.Globalization;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The queue's tenant-facing views: the app role reads the mechanism only through them, so
/// their shape is part of the boundary. Each view exposes the columns the module statements
/// name — the payload is projected to <c>artifact_id</c>, never carried — and the keyed join of
/// outbox to task stays inside the same barrier.
/// </summary>
[Collection(IsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class QueueViewTests
{
    private static readonly string[] Views =
        ["public.task_in_scope", "public.outbox_in_scope", "public.outbox_task_in_scope"];

    private readonly PostgresIsolationFixture _fixture;

    public QueueViewTests(PostgresIsolationFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Views_ExposeTheirContractColumns_AndNeverThePayload()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await ViewColumnsAsync(database, "task_in_scope"))
            .ShouldBe(["id", "org_id", "task_type", "state", "idempotency_key", "artifact_id"]);
        (await ViewColumnsAsync(database, "outbox_in_scope"))
            .ShouldBe(["id", "org_id", "task_type", "dispatched_at", "idempotency_key", "artifact_id"]);
        (await ViewColumnsAsync(database, "outbox_task_in_scope"))
            .ShouldBe(["id", "org_id", "idempotency_key", "dispatched_at", "task_state", "last_error"]);

        var exposed = await _fixture.ColumnAsync<string>(
            database,
            """
            select table_name || '.' || column_name from information_schema.columns
             where table_schema = 'public'
               and table_name in ('task_in_scope', 'outbox_in_scope', 'outbox_task_in_scope')
               and column_name = 'payload'
            """);
        exposed.ShouldBeEmpty("the payload must not survive a select on any scoped view");

        // The positive control: the base tables do carry the column, so the scan above reads
        // the catalogue that has it.
        (await _fixture.ScalarAsync<bool>(
            database,
            """
            select exists (select 1 from information_schema.columns
                            where table_schema = 'public' and table_name = 'task' and column_name = 'payload')
            """)).ShouldBeTrue();
    }

    [Fact]
    public async Task Views_CarryTheBarrier_AndOnlyTheAppRoleReadsThem()
    {
        var database = await _fixture.CreateDatabaseAsync();

        foreach (var view in Views)
        {
            var reloptions = await _fixture.ScalarAsync<string>(
                database,
                $"select coalesce(array_to_string(reloptions, ','), '') from pg_class where oid = '{view}'::regclass");

            reloptions.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .ShouldContain(
                    "security_barrier=true",
                    $"{view}: a caller's predicate must not be pushed below the tenant filter");

            (await _fixture.ScalarAsync<bool>(
                database, $"select has_table_privilege('evidgate_app', '{view}', 'SELECT')"))
                .ShouldBeTrue($"{view}: the tenant path reads it");

            foreach (var privilege in new[] { "INSERT", "UPDATE", "DELETE", "TRUNCATE" })
            {
                (await _fixture.ScalarAsync<bool>(
                    database, $"select has_table_privilege('evidgate_app', '{view}', '{privilege}')"))
                    .ShouldBeFalse($"{view}: the tenant path is a read, never a write");
            }
        }

        var publicGrants = await _fixture.ColumnAsync<string>(
            database,
            """
            select table_name || ':' || privilege_type from information_schema.table_privileges
             where table_schema = 'public'
               and table_name in ('task_in_scope', 'outbox_in_scope', 'outbox_task_in_scope')
               and grantee = 'PUBLIC'
            """);
        publicGrants.ShouldBeEmpty("no grant on the tenant path reaches PUBLIC");

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker", "evidgate_worker_sched" })
        {
            foreach (var view in Views)
            {
                (await _fixture.ScalarAsync<bool>(
                    database, $"select has_table_privilege('{role}', '{view}', 'SELECT')"))
                    .ShouldBeFalse($"{view}: {role} holds no standing read of the tenant path");
            }
        }
    }

    [Fact]
    public async Task ScopedViews_ProjectArtifactId_AndTheJoinCarriesTheTaskState()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var own = await SeedOrgAsync(database);
        var other = await SeedOrgAsync(database);
        var artifact = Guid.CreateVersion7();

        var joined = await InsertOutboxAsync(database, own, "view:joined", artifact);
        await InsertTaskAsync(database, own, joined, artifact, "done");
        await InsertOutboxAsync(database, own, "view:undispatched", artifact);
        await InsertOutboxAsync(database, other, "view:foreign", artifact);

        var app = _fixture.AppConnectionString(database);

        await using (var scope = await TenantScope.BeginAsync(app, own))
        {
            (await scope.ScalarAsync<string>(
                "select artifact_id from public.task_in_scope where idempotency_key = @key",
                ("key", "outbox:" + joined.ToString("D", CultureInfo.InvariantCulture))))
                .ShouldBe(artifact.ToString("D", CultureInfo.InvariantCulture));

            (await scope.ScalarAsync<string>(
                "select artifact_id from public.outbox_in_scope where idempotency_key = 'view:joined'"))
                .ShouldBe(artifact.ToString("D", CultureInfo.InvariantCulture));

            // One row per outbox row, and the task half appears only once the dispatcher wrote
            // it: the join is keyed on the dedupe key and keeps the undispatched row.
            (await scope.ScalarAsync<string>(
                "select string_agg(coalesce(task_state, '<none>'), ',' order by idempotency_key) "
                + "from public.outbox_task_in_scope"))
                .ShouldBe("done,<none>");
        }

        await using (var foreign = await TenantScope.BeginAsync(app, other))
        {
            (await foreign.ScalarAsync<long>("select count(*) from public.outbox_task_in_scope"))
                .ShouldBe(1, "another org's outbox row is filtered by the view's predicate");
        }
    }

    /// <summary>The column names of one public view, in declaration order.</summary>
    private Task<IReadOnlyList<string>> ViewColumnsAsync(string database, string view) =>
        _fixture.ColumnAsync<string>(
            database,
            $"""
            select column_name from information_schema.columns
             where table_schema = 'public' and table_name = '{view}'
             order by ordinal_position
            """);

    private Task<Guid> SeedOrgAsync(string database) => RunAsync(async connection =>
    {
        var org = Guid.CreateVersion7();
        await using var command = connection.CreateCommand();
        command.CommandText = "insert into public.organization (id, name, slug) values (@id, 'views', @slug)";
        command.Parameters.AddWithValue("@id", org);
        command.Parameters.AddWithValue("@slug", "views-" + org.ToString("N"));
        await command.ExecuteNonQueryAsync(Token);
        return org;
    }, _fixture.OwnerConnectionString(database));

    /// <summary>One outbox row as its producer writes it: only the app role holds that INSERT.</summary>
    private Task<Guid> InsertOutboxAsync(string database, Guid org, string key, Guid artifact) => RunAsync(async connection =>
    {
        var id = Guid.CreateVersion7();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key)
            values (@id, @org, 'artifact', @aggregate, 'artifact_parse',
                    jsonb_build_object('artifact_id', @artifact), @key)
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@org", org);
        command.Parameters.AddWithValue("@aggregate", artifact);
        command.Parameters.AddWithValue("@artifact", artifact.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@key", key);
        await command.ExecuteNonQueryAsync(Token);
        return id;
    }, _fixture.OwnerConnectionString(database));

    /// <summary>The dispatcher's task row for <paramref name="outboxId"/>, as the queue role writes it.</summary>
    private Task<bool> InsertTaskAsync(string database, Guid org, Guid outboxId, Guid artifact, string state) =>
        RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                insert into public.task (id, org_id, queue, task_type, payload, idempotency_key, state)
                values (@id, @org, 'default', 'artifact_parse',
                        jsonb_build_object('artifact_id', @artifact), @key, @state)
                """;
            command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("@org", org);
            command.Parameters.AddWithValue("@artifact", artifact.ToString("D", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(
                "@key",
                "outbox:" + outboxId.ToString("D", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@state", state);
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
