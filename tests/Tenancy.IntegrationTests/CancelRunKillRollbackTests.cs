// The request row and its kill row are written on the cancel transaction's own connection, so a kill insert that
// fails must take the whole cancel with it: not a request row, not a kill row, not the state change. The fault is a
// test-owned trigger on public.task for the kill task type only, on a database of its own — a trigger on a whole
// table would fail every other test's task insert, and the assertions count rows the rest of the suite writes too.
#pragma warning disable CA1861
using System.Net;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The atomicity of an accepted cancel: the transition, the <c>run_cancel_request</c> row and the kill <c>task</c> row
/// commit as one unit, so a failure between the request insert and the commit leaves a run that is not canceled and no
/// trace of the attempt — and a retry is a fresh, successful cancel. The retry's read-back then proves the two rows were
/// written by one transaction, which is what "on the cancel transaction's own connection" means.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CancelRunKillRollbackTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task CancelRun_KillTaskInsertFails_LeavesNoRequestAndNoTaskRow()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(
            fixture,
            database: database,
            configuration: CancelRunKillScheduleTests.Windows("00:00:01", "00:00:02"));
        var runId = await CancelRunTestData.RunInStateAsync(fixture, world, "running");

        // One key for both attempts: the aborted attempt's idempotency claim is written in the same transaction, so a
        // retry that is refused as a duplicate would mean the failure left something behind.
        var key = RegistryApi.NewKey();

        await ArmFaultAsync(world.Database);
        try
        {
            var crashed = await CancelRunTestData.CancelAsync(world, runId, idempotencyKey: key);

            crashed.Status.ShouldBe(HttpStatusCode.InternalServerError, crashed.ErrorMessage);
            crashed.ErrorCode.ShouldBe("internal_error");

            // Nothing of the aborted cancel survives — not the transition, not the outbox row it wrote, not the
            // request row, and not the schedule that follows it.
            (await CancelRunTestData.StateAsync(fixture, world, runId))
                .ShouldBe("running", "the canceled transition rolled back with the kill row it preceded");
            (await CancelRunTestData.VersionAsync(fixture, world, runId))
                .ShouldBe(1, "the run's only applied transition is the one that drove it to running");
            (await CancelRunTestData.OutboxTargetsAsync(fixture, world, runId))
                .ShouldBe(["running"], "the cancel's own state_changed row went down with its transaction");
            (await CancelRunTestData.RowCountAsync(fixture, world, runId))
                .ShouldBe(0, "the request row is written on the same transaction as the kill row");
            (await KillTaskCountAsync(world.Database, runId)).ShouldBe(0);
            (await IdempotencyRecordsAsync(world.Database, world.Org, key))
                .ShouldBe(0, "the key was claimed in the same transaction, so the retry below begins afresh");
        }
        finally
        {
            await DisarmFaultAsync(world.Database);
        }

        // The fault is gone: the same request, the same key, one fresh cancel — and now exactly one of each row.
        var retried = await CancelRunTestData.CancelAsync(world, runId, idempotencyKey: key);

        retried.Status.ShouldBe(HttpStatusCode.Accepted, retried.ErrorMessage);
        (await CancelRunTestData.StateAsync(fixture, world, runId)).ShouldBe("canceled");
        (await CancelRunTestData.RowCountAsync(fixture, world, runId)).ShouldBe(1);
        (await KillTaskCountAsync(world.Database, runId)).ShouldBe(1);
        (await CancelRunTestData.OutboxTargetsAsync(fixture, world, runId)).ShouldBe(["running", "canceled"]);

        // The two rows carry the inserting transaction's id, so an equal pair is the direct evidence that one
        // transaction — one connection, the cancel's own — wrote both.
        (await WriterOfAsync(world.Database, "run_cancel_request", $"org_id = '{world.Org:D}' and run_id = '{runId:D}'"))
            .ShouldBe(
                await WriterOfAsync(
                    world.Database,
                    "task",
                    $"task_type = '{CancelKillContract.TaskType}' and idempotency_key like '{KillKeyPrefix(runId)}'"),
                "the request row and the kill row were written by one transaction: they share its xmin");
    }

    /// <summary>
    /// The fault: a trigger that raises on every insert of the kill task type, so the failure lands after the request
    /// row of the same transaction and no other task row of this database is affected.
    /// </summary>
    private Task ArmFaultAsync(string database) =>
        fixture.ExecuteAsync(
            database,
            $"""
            create or replace function public.cancel_kill_rollback_fault_fn() returns trigger
              language plpgsql as $fault$
            begin
              raise exception 'cancel kill rollback: injected task insert failure' using errcode = 'P0001';
            end $fault$;
            drop trigger if exists cancel_kill_rollback_fault on public.task;
            create trigger cancel_kill_rollback_fault
              before insert on public.task
              for each row
              when (new.task_type = '{CancelKillContract.TaskType}')
              execute function public.cancel_kill_rollback_fault_fn();
            """);

    private Task DisarmFaultAsync(string database) =>
        fixture.ExecuteAsync(
            database,
            """
            drop trigger if exists cancel_kill_rollback_fault on public.task;
            drop function if exists public.cancel_kill_rollback_fault_fn();
            """);

    /// <summary>The kill rows of one run, found by the key prefix the producer derives from it.</summary>
    private Task<long> KillTaskCountAsync(string database, Guid runId) =>
        fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.task where task_type = '{CancelKillContract.TaskType}' "
            + $"and idempotency_key like '{KillKeyPrefix(runId)}'");

    /// <summary>The key prefix every kill row of one run carries: <c>…:{run}:cancel:kill:{request}</c>.</summary>
    private static string KillKeyPrefix(Guid runId) => $"orchestrator:run:{runId:D}:cancel:kill:%";

    /// <summary>
    /// The transaction that inserted one row, as PostgreSQL records it (<c>xmin</c>): two rows of one transaction share
    /// it, which is how a read-back proves they were written on one connection without reaching into the handler.
    /// </summary>
    private Task<string> WriterOfAsync(string database, string table, string predicate) =>
        fixture.ScalarAsync<string>(database, $"select xmin::text from public.{table} where {predicate}");

    /// <summary>
    /// The idempotency records one attempt's key owns in the tenant — a fresh key belongs to this test only.
    /// </summary>
    private Task<long> IdempotencyRecordsAsync(string database, Guid orgId, string key) =>
        fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.idempotency_record where org_id = '{orgId:D}' and key = '{key}'");
}
