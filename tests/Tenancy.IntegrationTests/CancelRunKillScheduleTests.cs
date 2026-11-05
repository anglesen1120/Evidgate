// 05.4.1-T2.1 through the real host and a real PostgreSQL: the request row and its one kill task row commit together,
// the real consumer fires the deadline, and the row — not a timer — is the schedule a restart resumes. Every consumer
// test runs on a database of its own: a poll claims every due row of a type the host handles, not only the row the test
// just enqueued.
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class CancelRunKillScheduleTests(PostgresTenancyFixture fixture)
{
    /// <summary>The queue an unrouted task type lands in (<c>TasksOptions.DispatchDefaultQueue</c>).</summary>
    private const string DefaultQueue = "default";

    /// <summary>The pair a host boots with; both keys are required and have no option default.</summary>
    internal static IReadOnlyDictionary<string, string?> Windows(string softStop, string kill) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = softStop,
            ["EVIDGATE:Runs:Cancel:KillDelay"] = kill,
        };

    /// <summary>
    /// The queue mechanism as the Worker composes it, with no hosted service, so nothing claims a row before the test
    /// does. Only the consumer tests register it, and each of them polls a database of its own.
    /// </summary>
    private static void AddTaskQueue(IServiceCollection services) =>
        services.AddEvidgateTasks(new ConfigurationBuilder().Build());

    // ---------------------------------------------------------------- schedule

    [Fact]
    public async Task CancelRun_FromRunning_SoftStopThenKillSchedule()
    {
        await using var world = await RunCreationTests.StartAsync(
            fixture, configuration: Windows("00:00:01", "00:00:02"));
        var runId = await CancelRunTestData.RunInStateAsync(fixture, world, "running");

        var answer = await CancelRunTestData.CancelAsync(world, runId);
        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);

        var row = await CancelRunTestData.RowAsync(fixture, world, runId);
        (row.SoftStopDeadlineAt - row.RequestedAt).ShouldBe(TimeSpan.FromSeconds(1));
        (row.KillAt - row.RequestedAt).ShouldBe(TimeSpan.FromSeconds(2));
        row.State.ShouldBe("requested");

        // The windows act on execution only: the run is terminal from the moment the request was accepted.
        (await CancelRunTestData.StateAsync(fixture, world, runId)).ShouldBe("canceled");

        var requestId = await RequestIdAsync(world.Database, world.Org, runId);
        (await TaskCountAsync(world.Database, runId)).ShouldBe(1);

        var task = await TaskAsync(world.Database, runId);
        task.TaskType.ShouldBe(CancelKillContract.TaskType);
        task.Queue.ShouldBe(DefaultQueue);
        task.State.ShouldBe("queued");
        task.Attempts.ShouldBe(0);
        task.OrgId.ShouldBe(world.Org);
        task.IdempotencyKey.ShouldBe(CancelKillContract.IdempotencyKey(runId, requestId));
        task.DueWithRequest.ShouldBeTrue(
            "the kill row is the schedule: its available_at is the request row's kill_at, not a timer");
    }

    [Fact]
    public async Task CancelRun_Repeat_EnqueuesOneKillTask()
    {
        await using var world = await RunCreationTests.StartAsync(
            fixture, configuration: Windows("00:00:01", "00:00:02"));
        var runId = await CancelRunTestData.RunInStateAsync(fixture, world, "running");

        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        // A second operator, a retried request: the request row's unique key is the arbiter, and the task's key names it.
        (await CancelRunTestData.CancelAsync(world, runId, reason: "second operator")).Status
            .ShouldBe(HttpStatusCode.Accepted);

        (await CancelRunTestData.RowCountAsync(fixture, world, runId))
            .ShouldBe(1, "one accepted cancel per run");
        (await TaskCountAsync(world.Database, runId)).ShouldBe(1, "a repeat cancel queues exactly one kill");
        (await CancelRunTestData.StateAsync(fixture, world, runId)).ShouldBe("canceled");
    }

    // ------------------------------------------------------------------ firing

    /// <summary>
    /// The deadline is the row: the real consumer claims the kill at <c>kill_at</c>, the request reaches <c>killed</c>
    /// and the run it names stays the <c>canceled</c> it became when the request was accepted.
    /// </summary>
    [Fact]
    public async Task CancelKillTask_FiresAtDeadline_MarksKilled()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database, "00:00:01", "00:00:01");

        var runId = await CancelRunTestData.RunInStateAsync(fixture, world, "running");
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);

        (await CancelRunTestData.RowAsync(fixture, world, runId)).State
            .ShouldBe("requested", "the graceful window is still open");

        await WaitForKillAtAsync(world.Database, world.Org, runId);
        (await ConsumeAsync(world)).ShouldBe(1, "the due kill row is the queue's own work");

        (await CancelRunTestData.RowAsync(fixture, world, runId)).State.ShouldBe("killed");
        (await CancelRunTestData.StateAsync(fixture, world, runId))
            .ShouldBe("canceled", "the kill acts on execution; run.state was terminal before it fired");

        var task = await TaskAsync(world.Database, runId);
        task.State.ShouldBe("done");
        task.Attempts.ShouldBe(1);
    }

    /// <summary>
    /// At-least-once delivery: the same kill row claimed a second time finds a final stage and completes, so a redelivery
    /// never raises (which would burn the retry cap) and never moves a stopped request again.
    /// </summary>
    [Fact]
    public async Task CancelKillTask_ReplayAfterKilled_IsNoOp()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database, "00:00:01", "00:00:01");

        var runId = await CancelRunTestData.RunInStateAsync(fixture, world, "running");
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        var requestId = await RequestIdAsync(world.Database, world.Org, runId);

        await WaitForKillAtAsync(world.Database, world.Org, runId);
        await InvokeAsync(world, runId, requestId);
        (await CancelRunTestData.RowAsync(fixture, world, runId)).State.ShouldBe("killed");

        await Should.NotThrowAsync(async () => await InvokeAsync(world, runId, requestId));
        (await CancelRunTestData.RowAsync(fixture, world, runId)).State.ShouldBe("killed");
    }

    /// <summary>
    /// A producer that confirmed its stop inside the graceful window leaves the request <c>soft_stopped</c>; the deadline
    /// fire then has nothing to kill, which is why a soft stop is never undone by <c>kill_at</c>.
    /// </summary>
    [Fact]
    public async Task CancelKillTask_SoftStoppedBeforeDeadline_DoesNotKill()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database, "00:00:01", "00:00:02");

        var runId = await CancelRunTestData.RunInStateAsync(fixture, world, "running");
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        var requestId = await RequestIdAsync(world.Database, world.Org, runId);

        // The stage a graceful stop records, written here as the owner through the guard's forward transitions.
        await fixture.ExecuteAsync(
            world.Database,
            $"update public.run_cancel_request set state = 'soft_stopping' where id = '{requestId:D}'");
        await fixture.ExecuteAsync(
            world.Database,
            $"update public.run_cancel_request set state = 'soft_stopped' where id = '{requestId:D}'");

        await WaitForKillAtAsync(world.Database, world.Org, runId);
        (await ConsumeAsync(world)).ShouldBe(1, "the row is still claimed; the handler decides it has nothing to do");

        (await CancelRunTestData.RowAsync(fixture, world, runId)).State
            .ShouldBe("soft_stopped", "a soft stop is never undone by the deadline");
        (await TaskAsync(world.Database, runId)).State
            .ShouldBe("done", "an already-stopped request is a completion, not a failure to retry");
        (await CancelRunTestData.StateAsync(fixture, world, runId)).ShouldBe("canceled");
    }

    /// <summary>
    /// The row is the schedule: the host that accepted the cancel is gone before <c>kill_at</c>, and a fresh host on the
    /// same database claims the persisted row at its <c>available_at</c> and fires it exactly once.
    /// </summary>
    [Fact]
    public async Task CancelKillTask_SurvivesWorkerRestart_TaskRowResumesFromAvailableAt()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        Guid runId;
        Guid orgId;
        Guid requestId;

        // Everything that knows about the cancel in memory dies with this host; only the two rows remain.
        await using (var before = await StartAsync(database, "00:00:01", "00:00:03"))
        {
            orgId = before.Org;
            runId = await CancelRunTestData.RunInStateAsync(fixture, before, "running");
            (await CancelRunTestData.CancelAsync(before, runId)).Status.ShouldBe(HttpStatusCode.Accepted);

            requestId = await RequestIdAsync(database, orgId, runId);
            var scheduled = await TaskAsync(database, runId);
            scheduled.State.ShouldBe("queued", "the row waits for its available_at, not for a timer");
            scheduled.Attempts.ShouldBe(0);
            scheduled.DueWithRequest.ShouldBeTrue();
            scheduled.IdempotencyKey.ShouldBe(CancelKillContract.IdempotencyKey(runId, requestId));
        }

        await using var after = await StartAsync(database, "00:00:01", "00:00:03");
        await WaitForKillAtAsync(database, orgId, runId);
        (await ConsumeAsync(after)).ShouldBe(1, "the persisted row is what the restarted host claims");

        (await CancelRunTestData.RowAsync(fixture, database, orgId, runId)).State.ShouldBe("killed");
        var fired = await TaskAsync(database, runId);
        fired.State.ShouldBe("done");
        fired.Attempts.ShouldBe(1, "one resume is one attempt, not one per host");
        (await TaskCountAsync(database, runId)).ShouldBe(1);
    }

    /// <summary>
    /// The payload of a task row is forgeable, the row policy is not: a kill row of another tenant that names this
    /// request is claimed and completed in its own tenant, and the request it names is not even a row there.
    /// </summary>
    [Fact]
    public async Task CancelKillTask_ForeignTenantRequest_NotKilled()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        await using var owner = await StartAsync(database, "00:00:01", "01:00:00");
        var runId = await CancelRunTestData.RunInStateAsync(fixture, owner, "running");
        (await CancelRunTestData.CancelAsync(owner, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        var requestId = await RequestIdAsync(database, owner.Org, runId);

        await using var other = await StartAsync(database, "00:00:01", "01:00:00");

        // The owner's own kill row is due in an hour, so the only row this poll can claim is the foreign one below.
        await fixture.ExecuteAsync(
            database,
            "insert into public.task (id, org_id, queue, task_type, payload, idempotency_key, state, attempts, "
            + "max_attempts, available_at) "
            + $"values ('{Guid.CreateVersion7():D}', '{other.Org:D}', '{DefaultQueue}', "
            + $"'{CancelKillContract.TaskType}', '{CancelKillContract.PayloadJson(runId, requestId)}'::jsonb, "
            + $"'{CancelKillContract.IdempotencyKey(runId, requestId)}:foreign', 'queued', 0, 8, now())");

        (await ConsumeAsync(other)).ShouldBe(1, "the foreign row is claimed and run in its own tenant");

        (await CancelRunTestData.RowAsync(fixture, database, owner.Org, runId)).State
            .ShouldBe("requested", "another tenant's request is not a row under this policy");
        (await ForeignTaskStateAsync(database, runId, requestId))
            .ShouldBe("done", "a request the tenant cannot see is a no-op, not a failure to retry");
        (await VisibleRowsAsync(other, requestId)).ShouldBe(0, "the row policy hides the request from the foreign tenant");
    }

    // ----------------------------------------------------------------- harness

    /// <summary>One world whose host composes the queue, so the tests can drive the real consumer.</summary>
    private Task<RunCreationTests.World> StartAsync(DedicatedDatabase database, string softStop, string kill) =>
        RunCreationTests.StartAsync(
            fixture,
            database: database,
            configuration: Windows(softStop, kill),
            services: AddTaskQueue);

    /// <summary>The one claim-and-run pass the Worker's poller makes.</summary>
    private static Task<int> ConsumeAsync(RunCreationTests.World world) =>
        world.Api.Services.GetRequiredService<TaskConsumer>()
            .PollOnceAsync(DefaultQueue, TestContext.Current.CancellationToken);

    /// <summary>
    /// The handler the consumer would run, invoked on a scope of the request's tenant and committed as the consumer
    /// commits it: what a redelivery of the same row does, without re-queueing the row.
    /// </summary>
    private static async Task InvokeAsync(RunCreationTests.World world, Guid runId, Guid requestId)
    {
        var token = TestContext.Current.CancellationToken;
        var invoker = world.Api.Services.GetServices<ITaskHandlerInvoker>()
            .Single(candidate => candidate.TaskType == CancelKillContract.TaskType);
        var factory = world.Api.Services.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await factory.BeginAsync(world.Org, cancellationToken: token);
        await invoker.InvokeAsync(CancelKillContract.PayloadJson(runId, requestId), scope, token);
        await scope.CommitAsync(token);
    }

    /// <summary>Waits for the database's own clock to reach the row's <c>kill_at</c> (the claim compares against it).</summary>
    private async Task WaitForKillAtAsync(string database, Guid orgId, Guid runId)
    {
        var token = TestContext.Current.CancellationToken;
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < giveUp)
        {
            var due = await fixture.ScalarAsync<bool>(
                database,
                "select coalesce((select now() >= kill_at from public.run_cancel_request "
                + $"where org_id = '{orgId:D}' and run_id = '{runId:D}'), false)");
            if (due)
            {
                return;
            }

            await Task.Delay(100, token);
        }

        throw new InvalidOperationException($"kill_at of run {runId:D} was not reached within 30 s");
    }

    /// <summary>The one cancel request row of the run, as the table stores it.</summary>
    private Task<Guid> RequestIdAsync(string database, Guid orgId, Guid runId) =>
        fixture.ScalarAsync<Guid>(
            database,
            $"select id from public.run_cancel_request where org_id = '{orgId:D}' and run_id = '{runId:D}'");

    /// <summary>The kill rows of one run, found by the key the producer derives from the run and the request.</summary>
    private Task<long> TaskCountAsync(string database, Guid runId) =>
        fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.task where task_type = '{CancelKillContract.TaskType}' "
            + $"and idempotency_key like 'orchestrator:run:{runId:D}:cancel:kill:%'");

    /// <summary>The one kill row of the run and whether its <c>available_at</c> is the request's <c>kill_at</c>.</summary>
    private async Task<KillTaskRow> TaskAsync(string database, Guid runId)
    {
        await using var connection = await fixture.OpenSuperuserAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select t.id, t.org_id, t.queue, t.task_type, t.state, t.attempts, t.idempotency_key,
                   t.available_at = r.kill_at as due_with_request
              from public.task t
              join public.run_cancel_request r
                on r.id = (t.payload ->> 'cancel_request_id')::uuid
             where t.task_type = @task_type
               and t.idempotency_key like @key_prefix
            """;
        command.Parameters.AddWithValue("task_type", CancelKillContract.TaskType);
        command.Parameters.AddWithValue("key_prefix", $"orchestrator:run:{runId:D}:cancel:kill:%");

        var token = TestContext.Current.CancellationToken;
        await using var reader = await command.ExecuteReaderAsync(token);
        (await reader.ReadAsync(token)).ShouldBeTrue($"no kill task row for run {runId:D}");

        var row = new KillTaskRow(
            await reader.GetFieldValueAsync<Guid>(0, token),
            await reader.GetFieldValueAsync<Guid>(1, token),
            await reader.GetFieldValueAsync<string>(2, token),
            await reader.GetFieldValueAsync<string>(3, token),
            await reader.GetFieldValueAsync<string>(4, token),
            await reader.GetFieldValueAsync<int>(5, token),
            await reader.GetFieldValueAsync<string>(6, token),
            await reader.GetFieldValueAsync<bool>(7, token));

        (await reader.ReadAsync(token)).ShouldBeFalse("one accepted cancel queues one kill");

        return row;
    }

    /// <summary>The state of the forged row of another tenant, found by the key this test gave it.</summary>
    private Task<string> ForeignTaskStateAsync(string database, Guid runId, Guid requestId) =>
        fixture.ScalarAsync<string>(
            database,
            "select state from public.task where idempotency_key = "
            + $"'{CancelKillContract.IdempotencyKey(runId, requestId)}:foreign'");

    /// <summary>How many request rows a tenant's own policy lets it see.</summary>
    private static async Task<long> VisibleRowsAsync(RunCreationTests.World world, Guid requestId)
    {
        var token = TestContext.Current.CancellationToken;
        var factory = world.Api.Services.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await factory.BeginAsync(world.Org, cancellationToken: token);
        await using var command = scope.Connection.CreateCommand();
        command.Transaction = scope.Transaction;
        command.CommandText = "select count(*) from public.run_cancel_request where id = @id";
        command.Parameters.AddWithValue("id", requestId);

        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    /// <summary>One kill row, the way the queue stores it.</summary>
    internal sealed record KillTaskRow(
        Guid Id,
        Guid OrgId,
        string Queue,
        string TaskType,
        string State,
        int Attempts,
        string IdempotencyKey,
        bool DueWithRequest);
}
