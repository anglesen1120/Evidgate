using Evidgate.BuildingBlocks.Persistence.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// `public.platform_sweep_audit` on real PostgreSQL: one write path (the owner definer the sweep
/// role alone may execute), no direct access for any runtime role, and an append-only trail at two
/// layers — no owner UPDATE/DELETE policy, and a guard trigger that refuses any statement row level
/// security does not filter. The grants are the production ones, so this is the surface the purge
/// handler's audit write actually meets.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class PlatformSweepAuditAccessTests
{
    private readonly PostgresTasksFixture _fixture;

    public PlatformSweepAuditAccessTests(PostgresTasksFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The table has one write path and no direct access: the sweep role inserts and reads only
    /// through the definer (which stamps its own actor), the app role holds nothing and cannot
    /// execute the definer, the operator holds the read grant and reads zero rows, and the trail
    /// is append-only at two layers: no owner UPDATE/DELETE policy, and the guard trigger for a
    /// role row level security does not filter, such as the superuser.
    /// </summary>
    [Fact]
    public async Task PlatformSweepAudit_IsWritableOnlyThroughTheDefiner()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var worker = _fixture.WorkerSchedConnectionString(database);
        var app = _fixture.AppConnectionString(database);
        var @operator = _fixture.OperatorConnectionString(database);

        var id = await TaskSql.ScalarAsync<Guid>(
            worker,
            "select evidgate.record_sweep_audit(@action, @removed, @limit, @budget_exhausted)",
            ("action", EvidgateSweeps.IdempotencyPurgeTaskType),
            ("removed", 1),
            ("limit", 10),
            ("budget_exhausted", false));

        id.ShouldNotBe(Guid.Empty);
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select actor from public.platform_sweep_audit where id = @id",
            ("id", id))).ShouldBe("evidgate_worker_sched", "the definer stamps the actor; the caller cannot");
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select action from public.platform_sweep_audit where id = @id",
            ("id", id))).ShouldBe(EvidgateSweeps.IdempotencyPurgeTaskType);

        // The operator holds the SELECT grant and no policy, so FORCE row level security gives
        // it zero rows until 02.8.1's operator scope opens the grant (0052's posture).
        (await TaskSql.ScalarAsync<long>(@operator, "select count(*) from public.platform_sweep_audit"))
            .ShouldBe(0, "the operator's read is grant-gated, not policy-scoped");

        // The sweep role's write arrives through EXECUTE only: it holds no table privilege.
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ExecuteAsync(
                worker,
                """
                insert into public.platform_sweep_audit (actor, action, removed, batch_limit, budget_exhausted)
                values ('evidgate_worker_sched', 'idempotency.purge', 0, 10, false)
                """));
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ScalarAsync<long>(worker, "select count(*) from public.platform_sweep_audit"));

        // The app role holds nothing on the table and cannot reach the definer.
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ScalarAsync<long>(app, "select count(*) from public.platform_sweep_audit"));
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ExecuteAsync(
                app,
                """
                insert into public.platform_sweep_audit (actor, action, removed, batch_limit, budget_exhausted)
                values ('evidgate_worker_sched', 'idempotency.purge', 0, 10, false)
                """));
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ScalarAsync<Guid>(
                app,
                "select evidgate.record_sweep_audit('idempotency.purge', 0, 10, false)"));

        // Append-only for every statement that reaches a row. The owner holds insert and select
        // policies only, so FORCE row level security leaves its UPDATE and DELETE zero rows and
        // the guard trigger never fires for them; the superuser bypasses RLS, so there the guard
        // trigger is what refuses.
        (await TaskSql.ExecuteAsync(owner, "update public.platform_sweep_audit set removed = 0"))
            .ShouldBe(0, "no UPDATE policy admits the owner");
        (await TaskSql.ExecuteAsync(owner, "delete from public.platform_sweep_audit"))
            .ShouldBe(0, "no DELETE policy admits the owner");
        (await CountAuditAsync(owner)).ShouldBe(1, "the owner's statements left the row in place");
        (await TaskSql.ScalarAsync<int>(
            owner,
            "select removed from public.platform_sweep_audit where id = @id",
            ("id", id))).ShouldBe(1, "the row is intact");

        await using var superuser = await _fixture.OpenSuperuserAsync(database);
        (await TaskSql.ScalarAsync<long>(superuser, "select count(*) from public.platform_sweep_audit"))
            .ShouldBe(1, "the superuser reaches the row, so the refusals below are the trigger's");
        await AssertSqlStateAsync("23514", async () => await TaskSql.ExecuteAsync(
            superuser,
            "update public.platform_sweep_audit set removed = 0"));
        await AssertSqlStateAsync("23514", async () => await TaskSql.ExecuteAsync(
            superuser,
            "delete from public.platform_sweep_audit"));

        (await CountAuditAsync(owner)).ShouldBe(1, "the refused statements left the row untouched");
    }

    private static Task<long> CountAuditAsync(string connectionString) =>
        TaskSql.ScalarAsync<long>(connectionString, "select count(*) from public.platform_sweep_audit");

    private static async Task AssertSqlStateAsync(string expected, Func<Task> statement)
    {
        var exception = await Should.ThrowAsync<PostgresException>(statement);
        exception.SqlState.ShouldBe(expected);
    }
}
