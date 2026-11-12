using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    [Fact]
    public async Task ReportReplay_FullCounter_CompletesWithoutGateOrMetering()
    {
        var spy = new QuotaGateSpy();
        await using var world = await StartAsync(services => ReplaceQuotaGate(services, inner => new SpyQuotaGate(inner, spy)));
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 1m);

        (await ParseAsync(world, artifact)).Events.ShouldBe(1);
        var full = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        full.Used.ShouldBe(full.Limit);
        var usageRows = await CountOutboxAsync(world, "usage.batch_accepted");
        usageRows.ShouldBe(1);

        var replay = await ParseAsync(world, artifact);

        replay.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        replay.Events.ShouldBe(0);
        spy.Calls.ShouldBe(1);
        (await CountOutboxAsync(world, "usage.batch_accepted")).ShouldBe(usageRows);
        (await CountRawReportsAsync(world, artifact)).ShouldBe(1);
        (await CountAsync(world, "event_batch")).ShouldBe(1);
        var after = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        after.Used.ShouldBe(full.Used);
        after.Version.ShouldBe(full.Version);
        after.BlockedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ReportTask_DeferredThenRaisedAndDue_CompletesAtOriginalAttempt(int maxAttempts)
    {
        // A pinned mid-month clock keeps the deferral clamp deterministic, so a run on the last day of a month cannot
        // turn this case's wait into a sub-minute one. It does not move the billing period: the gate charges the
        // database's month, and the counter below is seeded there.
        await using var world = await StartAsync(
            services => UseClock(services, new FakeTimeProvider(MidMarch)));
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 1m, limit: 1m);
        await using var worker = StartReportWorker(world);
        var consumer = worker.GetRequiredService<TaskConsumer>();
        var queue = "report-quota-probe";
        var taskId = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, $$"""
            insert into public.task (id, org_id, queue, task_type, payload, idempotency_key, max_attempts)
            values ('{{taskId:D}}', '{{world.Org:D}}', '{{queue}}', '{{EvidenceTaskTypes.ReportParse}}',
                '{"run_id":"{{world.RunId:D}}","artifact_id":"{{artifact:D}}","producer_kind":"pytest-junitxml"}'::jsonb,
                'report-quota:{{taskId:D}}', {{maxAttempts}})
            """);

        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskStateAsync(world, taskId)).ShouldBe("retry");
        (await TaskAttemptsAsync(world, taskId)).ShouldBe(0);
        (await CountRawReportsAsync(world, artifact)).ShouldBe(0);
        (await RunStateAsync(world)).ShouldBe("queued");
        var availableAt = await _fixture.ScalarAsync<DateTime>(world.Database,
            $"select available_at from public.task where id = '{taskId:D}'");
        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(0);

        (await RaiseAsync(counters, world, world.Project, 10m)).IsSuccess.ShouldBeTrue();
        (await _fixture.ScalarAsync<DateTime>(world.Database,
            $"select available_at from public.task where id = '{taskId:D}'")).ShouldBe(availableAt);
        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(0, "raising a limit does not itself advance available_at");
        await _fixture.ExecuteAsync(world.Database,
            $"update public.task set available_at = now() - interval '1 second' where id = '{taskId:D}'");

        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskStateAsync(world, taskId)).ShouldBe("done");
        (await TaskAttemptsAsync(world, taskId)).ShouldBe(1);
        (await RunStateAsync(world)).ShouldBe("running");
        (await CountAsync(world, "event", artifact)).ShouldBe(1);
        (await CountRawReportsAsync(world, artifact)).ShouldBe(1);
        (await CountOutboxAsync(world, "usage.batch_accepted")).ShouldBe(1);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(2m);
        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(0);
    }

    [Fact]
    public async Task ReportAndIngest_MultiUnitRace_ReserveWholeBatchesWithoutOvershoot()
    {
        await using var world = await StartAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world, "beta", "gamma");
        var artifact = await SeedArtifactAsync(world, ThreeEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 3m);
        var ingestRun = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        await IngestTestWorld.SeedSlotsAsync(_fixture, world.Database, world.Org, ingestRun.RunId, 1, IngestTestWorld.Jti);
        await using var host = TenancyApiFactory.WithPrincipal(_fixture.AppConnectionString(world.Database),
            world.Org, world.Project, IngestTestWorld.Writer(projectScope: world.Project));
        using var client = host.CreateClient();
        var body = IngestQaRequests.Batch(
            IngestQaRequests.Event(Guid.CreateVersion7(), ingestRun.Executions[0].ExecutionKey, 1, type: "test.step", status: null),
            IngestQaRequests.Event(Guid.CreateVersion7(), ingestRun.Executions[0].ExecutionKey, 2, type: "test.step", status: null));

        var reportTask = ParseAsync(world, artifact);
        var ingestTask = IngestQaRequests.PostEventsAsync(client, ingestRun.RunId, body);
        await Task.WhenAll(reportTask, ingestTask);
        var report = await reportTask;
        var ingest = await ingestTask;

        report.Status.ShouldBeOneOf(ParseRawReportOutcome.Parsed, ParseRawReportOutcome.QuotaBlocked);
        ingest.Status.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
        var reportWon = report.Status == ParseRawReportOutcome.Parsed;
        (reportWon ^ (ingest.Status == HttpStatusCode.OK)).ShouldBeTrue();
        var counter = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        counter.Used.ShouldBe(reportWon ? 3m : 2m);
        counter.Used.ShouldBeLessThanOrEqualTo(counter.Limit);
        (await OrgCountAsync(world, "event")).ShouldBe(reportWon ? 3 : 2);
        (await OrgCountAsync(world, "event_batch")).ShouldBe(1);
        (await CountRawReportsAsync(world, artifact)).ShouldBe(reportWon ? 1 : 0);
        (await CountOutboxAsync(world, "usage.batch_accepted")).ShouldBe(1);
    }

    private ServiceProvider StartReportWorker(World world)
    {
        var services = new ServiceCollection();
        services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(_fixture.AppConnectionString(world.Database)));
        services.AddLogging();
        services.AddEvidgateTenancy();
        services.AddEvidgateTasks(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{TasksOptions.SectionName}:WorkerConnectionString"] = _fixture.WorkerConnectionString(world.Database),
            }).Build());
        services.AddSingleton<ITaskHandlerInvoker>(new TransactionlessTaskHandlerAdapter<ParseRawReportPayload>(CreateHandler(world)));
        return services.BuildServiceProvider();
    }

    private Task<string?> TaskStateAsync(World world, Guid taskId) =>
        _fixture.ScalarAsync<string?>(world.Database, $"select state from public.task where id = '{taskId:D}'");

    private Task<int> TaskAttemptsAsync(World world, Guid taskId) =>
        _fixture.ScalarAsync<int>(world.Database, $"select attempts from public.task where id = '{taskId:D}'");
}
