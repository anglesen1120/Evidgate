using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The operator dead-letter path after the role split: the app role executes the two
/// definer functions inside its own tenant transaction, the organization comes from
/// the transaction context rather than a parameter, and a replay appends one audit row.
/// </summary>
[Collection(IsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class DeadLetterFunctionTests
{
    private readonly PostgresIsolationFixture _fixture;

    public DeadLetterFunctionTests(PostgresIsolationFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The list is scoped inside the function: another org's dead letter never appears.</summary>
    [Fact]
    public async Task List_CrossOrg_ReturnsNoRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var own = await SeedOrgAsync(database);
        var other = await SeedOrgAsync(database);
        var foreignTask = await SeedDeadLetterAsync(database, other, "list:foreign");

        var app = _fixture.AppConnectionString(database);
        await using (var scope = await TenantScope.BeginAsync(app, own))
        {
            (await scope.ScalarAsync<long>(
                "select count(*) from evidgate.list_dead_letter_tasks(null, null, null, 10)")).ShouldBe(0);
        }

        await using (var scope = await TenantScope.BeginAsync(app, other))
        {
            (await scope.ScalarAsync<long>(
                "select count(*) from evidgate.list_dead_letter_tasks(null, null, null, 10)")).ShouldBe(1);
            (await scope.ScalarAsync<Guid>(
                "select id from evidgate.list_dead_letter_tasks(null, null, null, 10)")).ShouldBe(foreignTask);
        }
    }

    /// <summary>A tenant transaction is mandatory: without one the function refuses instead of listing every org.</summary>
    [Fact]
    public async Task List_WithoutTenant_Refuses()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var app = _fixture.AppConnectionString(database);

        await using var scope = await TenantScope.BeginAsync(app);
        var refused = await scope.CaptureErrorAsync(
            "select count(*) from evidgate.list_dead_letter_tasks(null, null, null, 10)");

        refused.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    /// <summary>A replay of another org's row is not found and leaves both the row and the audit chain untouched.</summary>
    [Fact]
    public async Task Replay_CrossOrg_NotRefusedButNotFound_AndNoAudit()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var own = await SeedOrgAsync(database);
        var other = await SeedOrgAsync(database);
        var foreignTask = await SeedDeadLetterAsync(database, other, "replay:foreign");
        var actor = Guid.CreateVersion7();

        var app = _fixture.AppConnectionString(database);
        await using (var scope = await TenantScope.BeginAsync(app, own))
        {
            (await scope.ScalarAsync<string>(
                "select outcome from evidgate.replay_dead_letter_task(@id, @actor)",
                ("id", foreignTask),
                ("actor", actor))).ShouldBe("not_found");
        }

        (await _fixture.ScalarAsync<string>(
            database, $"select state from public.task where id = '{foreignTask:D}'")).ShouldBe("dead_letter");
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.audit_log where object_id = '{foreignTask:D}'")).ShouldBe(0);
    }

    /// <summary>An own-org replay requeues the row and appends exactly one audit row naming the operator action.</summary>
    [Fact]
    public async Task Replay_OwnOrg_Requeues_AndWritesOneAuditRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var own = await SeedOrgAsync(database);
        var taskId = await SeedDeadLetterAsync(database, own, "replay:own");
        var actor = Guid.CreateVersion7();

        var app = _fixture.AppConnectionString(database);
        await using (var scope = await TenantScope.BeginAsync(app, own))
        {
            (await scope.ScalarAsync<string>(
                "select outcome from evidgate.replay_dead_letter_task(@id, @actor)",
                ("id", taskId),
                ("actor", actor))).ShouldBe("replayed");
        }

        var row = await _fixture.ColumnAsync<string>(
            database,
            $"select state || '|' || attempts::text || '|' || (locked_until is null)::text "
            + $"from public.task where id = '{taskId:D}'");
        row.ShouldBe(["queued|0|true"]);

        var audits = await _fixture.ColumnAsync<string>(
            database,
            $"select action || '|' || object_type || '|' || actor_id from public.audit_log "
            + $"where object_id = '{taskId:D}' order by seq");
        audits.ShouldBe([$"task.dead_letter_replay|task|{actor:D}"]);

        // The state guard is the replay's idempotency contract.
        await using (var scope = await TenantScope.BeginAsync(app, own))
        {
            (await scope.ScalarAsync<string>(
                "select outcome from evidgate.replay_dead_letter_task(@id, @actor)",
                ("id", taskId),
                ("actor", actor))).ShouldBe("not_dead_letter");
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.audit_log where object_id = '{taskId:D}'"))
            .ShouldBe(1, "only a replay that changed a row is audited");
    }

    /// <summary>Both functions are app-role capabilities; operator and sweep role have none.</summary>
    [Fact]
    public async Task Functions_AreGrantedToTheAppRoleOnly()
    {
        var database = await _fixture.CreateDatabaseAsync();

        foreach (var role in new[] { "evidgate_app", "evidgate_operator", "evidgate_worker_sched", "evidgate_worker" })
        {
            var list = await _fixture.ScalarAsync<bool>(
                database,
                $"select has_function_privilege('{role}', "
                + "'evidgate.list_dead_letter_tasks(text,timestamp with time zone,uuid,integer)', 'EXECUTE')");
            var replay = await _fixture.ScalarAsync<bool>(
                database,
                $"select has_function_privilege('{role}', 'evidgate.replay_dead_letter_task(uuid,uuid)', 'EXECUTE')");

            var expected = role == "evidgate_app";
            list.ShouldBe(expected, role);
            replay.ShouldBe(expected, role);
        }
    }

    private Task<Guid> SeedOrgAsync(string database) => RunAsync(async connection =>
    {
        var org = Guid.CreateVersion7();
        await using var command = connection.CreateCommand();
        command.CommandText = "insert into public.organization (id, name, slug) values (@id, 'dead-letter', @slug)";
        command.Parameters.AddWithValue("@id", org);
        command.Parameters.AddWithValue("@slug", "dl-" + org.ToString("N"));
        await command.ExecuteNonQueryAsync(Token);
        return org;
    }, _fixture.OwnerConnectionString(database));

    private Task<Guid> SeedDeadLetterAsync(string database, Guid org, string key) => RunAsync(async connection =>
    {
        var id = Guid.CreateVersion7();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into public.task
              (id, org_id, queue, task_type, payload, idempotency_key, state, attempts, max_attempts, last_error, created_at)
            values (@id, @org, 'ops', 'test.dead_letter', '{}'::jsonb, @key, 'dead_letter', 3, 3, 'boom', now() - interval '1 minute')
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@org", org);
        command.Parameters.AddWithValue("@key", key + ":" + id.ToString("N"));
        await command.ExecuteNonQueryAsync(Token);
        return id;
    }, _fixture.WorkerConnectionString(database));

    private static async Task<T> RunAsync<T>(Func<NpgsqlConnection, Task<T>> work, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        return await work(connection);
    }
}
