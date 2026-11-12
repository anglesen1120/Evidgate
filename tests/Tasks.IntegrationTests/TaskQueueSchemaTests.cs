using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The shape of `task` — a mechanism table, so no row level security policy at
/// all — the exact index set the claim, the lease reclaim, the scheduler, the
/// operator list, the retention sweep and the evidence freeze's parse anti-join
/// are built on, and the grants that keep `evidgate_operator` and the sweep role
/// out of it.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskQueueSchemaTests
{
    private readonly PostgresTasksFixture _fixture;

    public TaskQueueSchemaTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// `task` carries `org_id` but no policy: the claim scans a whole queue and the
    /// dispatcher scans across orgs by design, so a tenant policy would silently hide
    /// work (the tenant boundary is the handler's scope, §8 S1). With no policy, an
    /// operator SELECT grant would instead be standing cross-org read access, which
    /// is why the grants test below revokes it.
    /// </summary>
    [Fact]
    public async Task Task_IsAMechanismTable_WithoutRowLevelSecurity()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        var security = await TaskSql.QueryAsync<bool>(
            owner,
            """
            select relrowsecurity or relforcerowsecurity
              from pg_class c join pg_namespace n on n.oid = c.relnamespace
             where n.nspname = 'public' and c.relname = 'task'
            """);
        security.ShouldHaveSingleItem().ShouldBeFalse("`task` is a mechanism table: no RLS, no policy (01.5.1 §3)");

        var indexes = await TaskSql.QueryAsync<string>(
            owner,
            """
            select indexname
              from pg_indexes
             where schemaname = 'public' and tablename = 'task'
             order by indexname
            """);
        indexes.ShouldBe(
        [
            "task_active_idx",
            "task_claim_idx",
            "task_claim_type_idx",
            "task_dead_idx",
            "task_fire_uq",
            "task_idem_uq",
            "task_parse_artifact_idx",
            "task_pkey",
            "task_terminal_idx",
        ]);
    }

    /// <summary>
    /// The app role is the only DML role in the system and runs the mechanism; the
    /// operator role has no audit-gated path into the table yet (the dead-letter list
    /// of 01.5.1-T4.2 adds what it needs), and the sweep role never touches a table
    /// (it exists to call owner functions, T80).
    /// </summary>
    [Fact]
    public async Task Task_Grants_Follow_LeastPrivilege()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        var app = await TaskSql.QueryAsync<string>(
            owner,
            """
            select p from unnest(array['select', 'insert', 'update', 'delete']) as p
             where has_table_privilege('evidgate_app', 'public.task', p)
             order by 1
            """);
        app.ShouldBe(["insert"], "the producer keeps INSERT; the queue role owns the DML");

        var worker = await TaskSql.QueryAsync<string>(
            owner,
            """
            select p from unnest(array['select', 'insert', 'update', 'delete']) as p
             where has_table_privilege('evidgate_worker', 'public.task', p)
             order by 1
            """);
        worker.ShouldBe(["delete", "insert", "select", "update"], "the queue role is the only DML role");

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            var granted = await TaskSql.ScalarAsync<bool>(
                owner,
                $"select has_table_privilege('{role}', 'public.task', 'select')");

            granted.ShouldBeFalse($"{role} must not read the mechanism table");
        }
    }

    /// <summary>
    /// The check constraints of 0008 are the mechanism's last line of defence against
    /// a producer: an undefined state, a half-set fire key (which no schedule runner
    /// could ever match again) and an unbounded key are all refused by the database,
    /// not by a convention in the writer.
    /// </summary>
    [Fact]
    public async Task Task_Constraints_Reject_InvalidRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        (await RejectsAsync(owner, ("state", "processing")))
            .ShouldBe("23514", "the state machine is closed: 0008's task_state_check");
        (await RejectsAsync(owner, ("schedule_name", "orphan")))
            .ShouldBe("23514", "a fire key is the pair: 0008's task_fire_pair_check");
        (await RejectsAsync(owner, ("idempotency_key", new string('k', 257))))
            .ShouldBe("23514", "producers are bounded: 0008's task_idempotency_key_length_check");

        static async Task<string?> RejectsAsync(string connectionString, (string Name, object Value) mutator)
        {
            var state = "queued";
            var scheduleName = (object?)null;
            var fireAt = (object?)null;
            var idempotencyKey = (object)$"reject:{Guid.NewGuid():N}";

            switch (mutator.Name)
            {
                case "state":
                    state = (string)mutator.Value;
                    break;
                case "schedule_name":
                    scheduleName = mutator.Value;
                    break;
                case "idempotency_key":
                    idempotencyKey = mutator.Value;
                    break;
                default:
                    throw new InvalidOperationException($"unhandled mutator '{mutator.Name}'");
            }

            var exception = await Should.ThrowAsync<PostgresException>(() => TaskSql.ExecuteAsync(
                connectionString,
                """
                insert into public.task
                  (id, queue, task_type, payload, idempotency_key, schedule_name, fire_at, state)
                values
                  (@id, 'schema', 'test.record', '{}'::jsonb, @key, @schedule_name, @fire_at, @state)
                """,
                ("id", Guid.CreateVersion7()),
                ("key", idempotencyKey),
                ("schedule_name", scheduleName),
                ("fire_at", fireAt),
                ("state", state)));

            return exception.SqlState;
        }
    }
}
