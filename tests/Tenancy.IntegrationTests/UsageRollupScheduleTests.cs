// The roll-up as the mechanism runs it: the schedule registration and its one-fire-per-instant election under two
// concurrent runners, and the platform handler that pages org ids through the sweep role's definer and recomputes each
// org inside its own tenant scope.
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage;
using Evidgate.Modules.Usage.Application.Rollup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// the registration itself. The sweep role's credential is the migration owner here: the function is called for its
/// rows and its ACL is proven separately, and no test in this suite may assume the deployment's secret.
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
        (await ScheduleRowsAsync(database)).ShouldBe([$"{UsageRollupSweeps.TaskType}|{UsageRollupSweeps.TaskType}|{EvidgateSweeps.MaintenanceQueue}"]);

        // The next pass at the same instant finds the occurrence taken and inserts nothing.
        await runner.RunOnceAsync(FiresAt, Token);
        (await ScheduleRowsAsync(database)).ShouldBe([$"{UsageRollupSweeps.TaskType}|{UsageRollupSweeps.TaskType}|{EvidgateSweeps.MaintenanceQueue}"]);
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

    private async Task<IReadOnlyList<string>> ScheduleRowsAsync(string database) =>
        await _fixture.ColumnAsync<string>(
            database,
            $"""
            select schedule_name || '|' || task_type || '|' || queue
            from public.task
            where schedule_name = '{UsageRollupSweeps.TaskType}'
            order by fire_at
            """);

    /// <summary>A host composing the production task mechanism over the module under test.</summary>
    private ServiceProvider StartHost(string database)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{TasksOptions.SectionName}:PollIntervalMilliseconds"] = "50",
            [$"{TasksOptions.SectionName}:BatchSize"] = "50",
            [$"{TasksOptions.SectionName}:VisibilityTimeoutSeconds"] = "300",
            [$"{TasksOptions.SectionName}:ScheduleLookbackSeconds"] = "300",
            [$"{TasksOptions.SectionName}:SweepConnectionString"] = _fixture.OwnerConnectionString(database),
        };

        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(_fixture.AppConnectionString(database)));
        services.AddLogging();
        services.AddEvidgateTenancy();
        services.AddEvidgateTasks(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddUsageModule();

        return services.BuildServiceProvider();
    }

    /// <summary>One org with one project and one ledger row, per entry, as the migration owner.</summary>
    private async Task<IReadOnlyList<Guid>> SeedOrgsAsync(string database, int count)
    {
        var orgs = new List<Guid>(count);
        for (var index = 0; index < count; index++)
        {
            var org = Guid.CreateVersion7();
            var project = Guid.CreateVersion7();
            var suffix = org.ToString("N")[..8];
            await _fixture.ExecuteAsync(
                database,
                $"""
                insert into public.organization (id, name, slug) values ('{org:D}', 'acme', 'acme-{suffix}');
                insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'pp', 'pp-{suffix}');
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
