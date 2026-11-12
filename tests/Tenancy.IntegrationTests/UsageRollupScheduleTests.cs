// The roll-up as the mechanism runs it: the schedule registration and its one-fire-per-instant election under two
// concurrent runners, and the platform handler that pages org ids through the sweep role's definer and recomputes each
// org inside its own tenant scope.
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage;
using Evidgate.Modules.Usage.Application.Rollup;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The scheduling half of plan §7: the handler is a platform task (<c>RequiresTenant</c> false) whose scan is the
/// definer over the ledger's distinct org ids, and its schedule fires once per occurrence however many workers run.
/// </summary>
/// <remarks>
/// The host composes the production mechanism (<c>AddEvidgateTasks</c> plus the Usage module), so the assertions cover
/// the registration itself. The fixture mints no login for the sweep role, so the host replaces the sweep data source
/// with a double that switches a superuser connection to <c>evidgate_worker_sched</c> — the same shape the reconcile
/// suites use — while the function's own ACL is proven by the schema suite.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageRollupScheduleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly PostgresTenancyFixture _fixture;

    public UsageRollupScheduleTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>The occurrence of the daily cron this suite fires at: 02:23 UTC, off every other sweep's minute.</summary>
    private static readonly DateTimeOffset FiresAt = new(2026, 2, 23, 2, 23, 0, TimeSpan.Zero);

    [Fact]
    public async Task Schedule_FiresOncePerOccurrence_WithTwoRunners()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        await using var provider = StartHost(database);

        var schedules = provider.GetRequiredService<IScheduleRegistry>();
        schedules.Schedules.ShouldContain(
            schedule => schedule.Name == UsageRollupSweeps.TaskType,
            "the module registers the recompute with the mechanism, not only in its own code");
        UsageRollupSweeps.Cron.ShouldBe("23 2 * * *");
        UsageRollupSweeps.Queue.ShouldBe(EvidgateSweeps.MaintenanceQueue);

        // Two runners, the same instant: the unique (schedule_name, fire_at) key elects one row.
        var runner = provider.GetRequiredService<TaskScheduleRunner>();
        await Task.WhenAll(runner.RunOnceAsync(FiresAt, Token), runner.RunOnceAsync(FiresAt, Token));
        (await ScheduleRowsAsync(database, UsageRollupSweeps.TaskType)).ShouldBe([$"{UsageRollupSweeps.TaskType}|{UsageRollupSweeps.TaskType}|{EvidgateSweeps.MaintenanceQueue}"]);

        // The next pass at the same instant finds the occurrence taken and inserts nothing.
        await runner.RunOnceAsync(FiresAt, Token);
        (await ScheduleRowsAsync(database, UsageRollupSweeps.TaskType)).ShouldBe([$"{UsageRollupSweeps.TaskType}|{UsageRollupSweeps.TaskType}|{EvidgateSweeps.MaintenanceQueue}"]);
    }

    [Fact]
    public async Task Handler_RecomputesEveryOrgTheLedgerNames_AcrossPages()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var orgs = await SeedOrgsAsync(database, count: 3);

        await using var provider = StartHost(database);
        var registry = provider.GetRequiredService<ITaskHandlerRegistry>();
        registry.TryGet(UsageRollupSweeps.TaskType, out var invoker).ShouldBeTrue();
        invoker!.RequiresTenant.ShouldBeFalse("the scan spans tenants and cannot run inside one");

        // A page of two over three orgs: the handler drains a second page, so every org is recomputed by one fire.
        await invoker.InvokeAsync("""{"org_batch":2}""", scope: null, Token);

        (await _fixture.ColumnAsync<string>(
            database,
            $"select org_id::text from public.usage_rollup order by org_id"))
            .ShouldBe([.. orgs.Order().Select(org => org.ToString("D"))]);
    }

    [Fact]
    public async Task Handler_LeavesOrgsWithoutLedgerRowsAlone()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var orgs = await SeedOrgsAsync(database, count: 2);
        var untouched = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug)
            values ('{untouched:D}', 'idle', 'idle-{untouched.ToString("N")[..8]}')
            """);

        await using var provider = StartHost(database);
        var registry = provider.GetRequiredService<ITaskHandlerRegistry>();
        registry.TryGet(UsageRollupSweeps.TaskType, out var invoker).ShouldBeTrue();
        await invoker!.InvokeAsync("""{"org_batch":100}""", scope: null, Token);

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.usage_rollup where org_id = '{untouched:D}'"))
            .ShouldBe(0, "the definer pages orgs with ledger rows, so an idle org is never visited");
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.usage_rollup"))
            .ShouldBe(orgs.Count);
    }

    [Fact]
    public async Task Handler_DrainsEveryPageUntilTheScanIsExhausted()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var orgs = await SeedOrgsAsync(database, count: 11);

        await using var provider = StartHost(database);
        var registry = provider.GetRequiredService<ITaskHandlerRegistry>();
        registry.TryGet(UsageRollupSweeps.TaskType, out var invoker).ShouldBeTrue();

        // One org per page over eleven orgs: a bounded fire would stop at the first page and starve ten tenants.
        await invoker!.InvokeAsync("""{"org_batch":1}""", scope: null, Token);

        (await _fixture.ColumnAsync<string>(
            database,
            "select org_id::text from public.usage_rollup order by org_id"))
            .ShouldBe([.. orgs.Order().Select(org => org.ToString("D"))]);
    }

    [Fact]
    public async Task Handler_RevisitsAnOrgWhoseLedgerRowsAreGone_AndFlagsTheStoredAggregate()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var orgs = await SeedOrgsAsync(database, count: 1);

        await using var provider = StartHost(database);
        var registry = provider.GetRequiredService<ITaskHandlerRegistry>();
        registry.TryGet(UsageRollupSweeps.TaskType, out var invoker).ShouldBeTrue();

        await invoker!.InvokeAsync("""{"org_batch":100}""", scope: null, Token);
        (await _fixture.ScalarAsync<bool>(
            database,
            $"select gap_flag from public.usage_rollup where org_id = '{orgs[0]:D}'"))
            .ShouldBeFalse();

        // The org's last ledger row is gone: only the stored aggregate still names the tenant, and the scan must
        // return it so the recompute can flag the total the facts no longer support.
        await _fixture.ExecuteAsync(
            database,
            $"delete from public.usage_ledger_entry where org_id = '{orgs[0]:D}'");
        await invoker.InvokeAsync("""{"org_batch":100}""", scope: null, Token);

        (await _fixture.ScalarAsync<bool>(
            database,
            $"select gap_flag from public.usage_rollup where org_id = '{orgs[0]:D}'"))
            .ShouldBeTrue("the scan walks orgs with an aggregate too, not only orgs with ledger rows");
    }

    [Fact]
    public async Task Handler_OneFailingOrg_DoesNotStarveTheOrgsBehindIt()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var orgs = await SeedOrgsAsync(database, count: 3);
        var failing = orgs.Order().First();

        await using var provider = StartHost(database, new FailingRecompute(failing));
        var registry = provider.GetRequiredService<ITaskHandlerRegistry>();
        registry.TryGet(UsageRollupSweeps.TaskType, out var invoker).ShouldBeTrue();

        var error = await Should.ThrowAsync<AggregateException>(
            async () => await invoker!.InvokeAsync("""{"org_batch":1}""", scope: null, Token));

        error.Message.ShouldContain("1 org(s)", Case.Sensitive);
        error.Message.ShouldContain(failing.ToString("D"), Case.Sensitive);
        error.InnerExceptions.Count.ShouldBe(1);

        var rolledUp = await _fixture.ColumnAsync<string>(
            database,
            "select org_id::text from public.usage_rollup order by org_id");
        rolledUp.ShouldBe(
            [.. orgs.Order().Skip(1).Select(org => org.ToString("D"))],
            "the fire continues past the failed tenant and the task queue is told to retry");
    }

    [Fact]
    public async Task Schedule_PeriodClose_FiresOncePerOccurrence_WithTwoRunners()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        await using var provider = StartHost(database);

        var schedules = provider.GetRequiredService<IScheduleRegistry>();
        schedules.Schedules.ShouldContain(
            schedule => schedule.Name == UsageRollupSweeps.PeriodCloseScheduleName,
            "the month boundary has its own schedule, registered with the mechanism");
        UsageRollupSweeps.PeriodCloseCron.ShouldBe("5 0 1 * *");
        UsageRollupSweeps.ComputeOnPeriodClose.TaskType.ShouldBe(UsageRollupSweeps.TaskType);
        UsageRollupSweeps.ComputeOnPeriodClose.Queue.ShouldBe(EvidgateSweeps.MaintenanceQueue);

        // 2026-11-01 00:05 UTC: the first day of a month, five minutes after the close.
        var closeFireAt = new DateTimeOffset(2026, 11, 1, 0, 5, 0, TimeSpan.Zero);
        var runner = provider.GetRequiredService<TaskScheduleRunner>();
        await Task.WhenAll(runner.RunOnceAsync(closeFireAt, Token), runner.RunOnceAsync(closeFireAt, Token));

        (await ScheduleRowsAsync(database, UsageRollupSweeps.PeriodCloseScheduleName)).ShouldBe(
            [$"{UsageRollupSweeps.PeriodCloseScheduleName}|{UsageRollupSweeps.TaskType}|{EvidgateSweeps.MaintenanceQueue}"]);

        // A third pass at the same instant finds the occurrence taken and inserts nothing more.
        await runner.RunOnceAsync(closeFireAt, Token);
        (await ScheduleRowsAsync(database, UsageRollupSweeps.PeriodCloseScheduleName)).ShouldBe(
            [$"{UsageRollupSweeps.PeriodCloseScheduleName}|{UsageRollupSweeps.TaskType}|{EvidgateSweeps.MaintenanceQueue}"]);
        (await ScheduleRowsAsync(database, UsageRollupSweeps.TaskType)).ShouldBeEmpty(
            "the daily schedule's 02:23 occurrence is outside the period-close window");
    }

    private async Task<IReadOnlyList<string>> ScheduleRowsAsync(string database, string scheduleName) =>
        await _fixture.ColumnAsync<string>(
            database,
            $"""
            select schedule_name || '|' || task_type || '|' || queue
            from public.task
            where schedule_name = '{scheduleName}'
            order by fire_at
            """);

    /// <summary>A host composing the production task mechanism over the module under test.</summary>
    /// <param name="database">The dedicated database.</param>
    /// <param name="recompute">The recompute seam to substitute, or null for the production store.</param>
    private ServiceProvider StartHost(string database, IUsageRollupRecompute? recompute = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{TasksOptions.SectionName}:PollIntervalMilliseconds"] = "50",
            [$"{TasksOptions.SectionName}:BatchSize"] = "50",
            [$"{TasksOptions.SectionName}:VisibilityTimeoutSeconds"] = "300",
            [$"{TasksOptions.SectionName}:ScheduleLookbackSeconds"] = "300",
            [$"{TasksOptions.SectionName}:WorkerConnectionString"] = _fixture.WorkerConnectionString(database),
        };

        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(_fixture.AppConnectionString(database)));
        services.AddLogging();
        services.AddEvidgateTenancy();
        services.AddEvidgateTasks(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddUsageModule();

        if (recompute is not null)
        {
            services.RemoveAll<IUsageRollupRecompute>();
            services.AddSingleton(recompute);
        }

        // The fixture mints no login for evidgate_worker_sched, and the production source refuses a credential that
        // names another role: the double opens a superuser connection and switches to the sweep role instead.
        services.RemoveAll<IWorkerSchedDataSource>();
        services.AddSingleton<IWorkerSchedDataSource>(_ => new SweepRoleDataSource(_fixture, database));

        return services.BuildServiceProvider();
    }

    /// <summary>The production recompute, except for one org, whose fire fails before it writes anything.</summary>
    private sealed class FailingRecompute(Guid orgId) : IUsageRollupRecompute
    {
        public Task RecomputeAsync(ITenantScope scope, CancellationToken cancellationToken) =>
            scope.OrgId == orgId
                ? throw new InvalidOperationException("the store refused this tenant")
                : UsageRollupStore.RecomputeAsync(scope, cancellationToken);
    }

    /// <summary>Opens the sweep role's connection: a superuser connection switched with <c>set role</c>.</summary>
    private sealed class SweepRoleDataSource(PostgresTenancyFixture fixture, string database) : IWorkerSchedDataSource
    {
        public async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await fixture.OpenSuperuserAsync(database).ConfigureAwait(false);
            try
            {
                await using var switchRole = connection.CreateCommand();
                switchRole.CommandText = "set role evidgate_worker_sched";
                await switchRole.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return connection;
        }
    }

    /// <summary>One org with one project and one ledger row, per entry, as the migration owner.</summary>
    private async Task<IReadOnlyList<Guid>> SeedOrgsAsync(string database, int count)
    {
        var orgs = new List<Guid>(count);
        for (var index = 0; index < count; index++)
        {
            var org = Guid.CreateVersion7();
            var project = Guid.CreateVersion7();
            // A v7 uuid's first hex digits are its timestamp, so two orgs seeded in one test would share them;
            // the shared helper draws an independent random suffix instead.
            var orgSlug = Unique.Slug("acme");
            var projectSlug = Unique.Slug("pp");
            await _fixture.ExecuteAsync(
                database,
                $"""
                insert into public.organization (id, name, slug) values ('{org:D}', 'acme', '{orgSlug}');
                insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'pp', '{projectSlug}');
                insert into public.usage_ledger_entry
                    (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at)
                values ('{Guid.CreateVersion7():D}', '{org:D}', '{project:D}', 'results', {index + 1}, 'batch',
                        '2026-02', 'sched:{Guid.CreateVersion7():N}', now());
                """);
            orgs.Add(org);
        }

        return orgs;
    }
}
