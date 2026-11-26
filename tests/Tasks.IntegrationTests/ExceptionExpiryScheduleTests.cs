using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application.Expiry;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The waiver and quarantine expiry schedule through the real task path: the runner inserts one platform task per cron
/// occurrence, the consumer runs the registered handler, and the handler lists orgs as the sweep role and expires each
/// org's due rows in a tenant transaction with their audit rows.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class ExceptionExpiryScheduleTests(PostgresTasksFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two independent host compositions over one database race one occurrence and then race to consume it: one task row
    /// is inserted, a second row for the same fire is refused by the unique key even under another idempotency key, the
    /// handler runs once, and each due waiver is expired once with one audit row.
    /// </summary>
    [Fact]
    public async Task ExpirySchedule_UniqueFireKey_OneTaskAndOneSweepPerFire()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        var runs = new SweepRuns();
        await using var first = Start(database, runs);
        await using var second = Start(database, runs);
        Guid[] waivers = [await SeedDueWaiverAsync(owner), await SeedDueWaiverAsync(owner), await SeedDueWaiverAsync(owner)];

        // Just after the registered hourly fire (minute 7 of 14:00 UTC).
        var at = new DateTimeOffset(2026, 1, 12, 14, 7, 30, TimeSpan.Zero);
        var fired = await Task.WhenAll(first.Runner.RunOnceAsync(at, Token), second.Runner.RunOnceAsync(at, Token));
        fired.Sum().ShouldBe(1, "two hosts racing one occurrence insert one task");
        (await first.Runner.RunOnceAsync(at, Token)).ShouldBe(0, "a later pass of the same occurrence inserts none");
        (await second.Runner.RunOnceAsync(at, Token)).ShouldBe(0, "and none from the other host either");

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where schedule_name = @n",
            ("n", ExpirySweeps.Name))).ShouldBe(1, "one task row for the one fire");
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select task_type from public.task where schedule_name = @n",
            ("n", ExpirySweeps.Name))).ShouldBe(ExpirySweeps.TaskType);
        (await TaskSql.ScalarAsync<bool>(
            owner,
            "select org_id is null from public.task where schedule_name = @n",
            ("n", ExpirySweeps.Name))).ShouldBeTrue("the scan spans every org: a platform task");

        var duplicate = await Should.ThrowAsync<PostgresException>(() => TaskSql.ExecuteAsync(
            owner,
            """
            insert into public.task (id, queue, task_type, payload, idempotency_key, schedule_name, fire_at)
            select @id, queue, task_type, payload, 'another-producer', schedule_name, fire_at
              from public.task where schedule_name = @n
            """,
            ("id", Guid.CreateVersion7()),
            ("n", ExpirySweeps.Name)));
        duplicate.ConstraintName.ShouldBe("task_fire_uq", "the unique fire key, not the idempotency key, elects the sweep");

        var claimed = await Task.WhenAll(
            first.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token),
            second.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token));
        claimed.Sum().ShouldBe(1, "two consumers racing for the one task claim it once");
        runs.Count.ShouldBe(1, "the handler ran once for the fire");
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select state from public.task where schedule_name = @n",
            ("n", ExpirySweeps.Name))).ShouldBe("done");

        foreach (var waiver in waivers)
        {
            (await TaskSql.ScalarAsync<string>(
                owner,
                "select state || ':' || version from public.waiver where id = @id",
                ("id", waiver))).ShouldBe("expired:2", "one transition per due row");
            (await TaskSql.ScalarAsync<long>(
                owner,
                "select count(*) from public.audit_log where action = 'waiver.expire' and object_id = @id",
                ("id", waiver.ToString("D")))).ShouldBe(1, "one audit row per transition, written by the one sweep");
        }

        (await first.Runner.RunOnceAsync(at.AddHours(1), Token)).ShouldBe(1, "the next hour's occurrence is its own fire");
    }

    /// <summary>Counts how often the real sweep handler is entered, across every host sharing the instance.</summary>
    private sealed class SweepRuns
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        internal void Record() => Interlocked.Increment(ref _count);
    }

    private sealed class CountingSweepHandler(SweepExpiredExceptionsTaskHandler inner, SweepRuns runs)
        : ITaskHandler<SweepExpiredExceptionsCommand>
    {
        public string TaskType => inner.TaskType;

        public bool RequiresTenant => inner.RequiresTenant;

        public ValueTask HandleAsync(SweepExpiredExceptionsCommand payload, ITenantScope? scope, CancellationToken cancellationToken)
        {
            runs.Record();
            return inner.HandleAsync(payload, scope, cancellationToken);
        }
    }

    private TasksTestHost Start(string database, SweepRuns runs) =>
        TasksTestHost.Start(
            fixture,
            database,
            services: services =>
            {
                services.TryAddSingleton<ExceptionExpiryStore>();
                services.AddSingleton(runs);
                services.AddSingleton<SweepExpiredExceptionsTaskHandler>();
                services.AddTaskHandler<SweepExpiredExceptionsCommand, CountingSweepHandler>();
                services.AddTaskSchedule(ExpirySweeps.Expire);
            });

    private static async Task<Guid> SeedDueWaiverAsync(string owner)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var waiver = Guid.CreateVersion7();
        await TaskSql.ExecuteAsync(
            owner,
            """
            insert into public.organization (id, name, slug) values (@org, 'expiry-org', 'expiry-' || @org::text);
            insert into public.project (id, org_id, name, slug) values (@project, @org, 'expiry-pp', 'expiry-pp');
            insert into public.waiver
              (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at)
            values (@waiver, @org, @project, 'case', @target, 'due waiver', @requester, @approver, 'approved',
                    now() - interval '1 minute')
            """,
            ("org", org),
            ("project", project),
            ("waiver", waiver),
            ("target", Guid.CreateVersion7()),
            ("requester", Guid.CreateVersion7()),
            ("approver", Guid.CreateVersion7()));
        return waiver;
    }
}
