// The report path's quota seam observed from outside the transaction: how many questions one batch asks, what a block
// does to the task (a deferral, never a spent attempt), how the deferral is clamped, and that a report and an ingest
// batch racing for the last unit of room never both commit.
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    /// <summary>The fixed instant the deferral cases pin the host's clock to: mid-March, 17 days before the rollover.</summary>
    private static readonly DateTimeOffset MidMarch = new(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task One_Question_Per_Report_Batch_And_None_For_A_Replay()
    {
        var spy = new QuotaGateSpy();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => new SpyQuotaGate(inner, spy)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world, "beta", "gamma");
        var artifactId = await SeedArtifactAsync(world, ThreeEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 100m);

        var outcome = await ParseAsync(world, artifactId);

        // Three events, one question: the gate charges a batch, never an event.
        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(3);
        spy.Calls.ShouldBe(1);
        var request = spy.Requests.ShouldHaveSingleItem();
        request.Metric.ShouldBe(UsageMetric.Results);
        request.Period.ShouldBe(Period(world));
        request.ProjectId.ShouldBe(world.Project, "the run's own project is the counter's owner");
        request.Quantity.ShouldBe(3m, "the quantity is exactly what the batch receipt meters");
        request.IncludeTenantScope.ShouldBeTrue("an org-wide limit must cap a single project's report too");
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(3m);

        // A replay inserted nothing, so it reserves nothing and asks nobody.
        (await ParseAsync(world, artifactId)).Events.ShouldBe(0);
        spy.Calls.ShouldBe(1, "the replay added no event and therefore no question");
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(3m);
    }

    [Fact]
    public async Task A_Report_That_Inserts_Nothing_Asks_No_Question()
    {
        var spy = new QuotaGateSpy();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => new SpyQuotaGate(inner, spy)));
        await using var _ = world;
        var artifactId = await SeedArtifactAsync(world, OneEventBody);

        // No plan at all, so every testcase is unmapped: the report row commits and the gate is never consulted.
        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(0);
        spy.Calls.ShouldBe(0);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_Blocked_Report_Defers_The_Same_Task_For_The_Gates_Wait()
    {
        var clock = new FakeTimeProvider(MidMarch);
        var world = await StartAsync(services => UseClock(services, clock));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        var payload = new ParseRawReportPayload(world.RunId, artifactId, ProducerKind);

        var blocked = await CreateHandler(world).ExecuteAsync(payload, world.Org, Token);

        blocked.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        blocked.RetryAfter.ShouldNotBeNull();

        // The task entry point answers a block with the deferral the queue understands: the same row waits for the
        // gate's wait instead of spending an attempt on a refusal only a rollover or an operator raise can lift.
        var deferred = await Should.ThrowAsync<TaskDeferredException>(
            async () => await CreateHandler(world).HandleAsync(payload, world.Org, Token));

        deferred.Delay.ShouldBe(
            ParseRawReportHandler.QuotaRetryDelay(blocked.RetryAfter, clock.GetUtcNow()),
            "the frozen clock makes the clamp deterministic");
        deferred.Delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        deferred.Delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromDays(17));
    }

    [Theory]
    [InlineData(null, 1, true)]
    [InlineData(0, 0, true)]
    [InlineData(-5, 0, true)]
    [InlineData(0, 500, false)]
    [InlineData(0, 5000, false)]
    [InlineData(5, 0, false)]
    [InlineData(40, 0, false)]
    public void The_Deferral_Delay_Is_Clamped_Between_One_Second_And_The_Next_Period(
        int? waitDays,
        int waitMilliseconds,
        bool refuses)
    {
        TimeSpan? retryAfter = waitDays is { } days
            ? TimeSpan.FromDays(days) + TimeSpan.FromMilliseconds(waitMilliseconds)
            : null;

        if (refuses)
        {
            // A block with no usable wait names no retry either: the ordinary failure path answers it, not the deferral.
            Should.Throw<InvalidOperationException>(
                () => ParseRawReportHandler.QuotaRetryDelay(retryAfter, MidMarch));
            return;
        }

        // The three bounds of a mid-March attempt: the gate's one-second floor lifts anything under it, the
        // fifteen-minute cap brings an attempt back in time for a raise or the run's late window, and a wait between
        // the two stands as asked.
        var expected = retryAfter < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : retryAfter > TimeSpan.FromMinutes(15)
                ? TimeSpan.FromMinutes(15)
                : retryAfter!.Value;

        ParseRawReportHandler.QuotaRetryDelay(retryAfter, MidMarch).ShouldBe(expected);
    }

    [Fact]
    public void The_Next_Period_Beats_The_Cap_When_It_Ends_Sooner()
    {
        var nearlyApril = new DateTimeOffset(2026, 3, 31, 23, 55, 0, TimeSpan.Zero);

        ParseRawReportHandler.QuotaRetryDelay(TimeSpan.FromMinutes(10), nearlyApril)
            .ShouldBe(TimeSpan.FromMinutes(5), "the month's own end is the shortest of the three bounds");
    }

    [Fact]
    public void The_Deferral_Floor_Wins_Over_A_Period_That_Ends_Within_The_Second()
    {
        var lastMoment = new DateTimeOffset(2026, 3, 31, 23, 59, 59, 500, TimeSpan.Zero);

        var delay = ParseRawReportHandler.QuotaRetryDelay(TimeSpan.FromMilliseconds(200), lastMoment);

        delay.ShouldBe(
            TimeSpan.FromSeconds(1),
            "the gate's floor is one second, and a wait past the rollover still cannot undercut it");
    }

    [Fact]
    public async Task A_Gate_That_Cannot_Answer_Refuses_The_Report_Instead_Of_Admitting_It_Unmetered()
    {
        var world = await StartAsync(services =>
        {
            services.RemoveAll<IQuotaGate>();
            services.AddScoped<IQuotaGate, ThrowingQuotaGate>();
        });
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);

        var failure = await Should.ThrowAsync<InvalidOperationException>(async () => await ParseAsync(world, artifactId));

        failure.Message.ShouldContain("quota store", Case.Sensitive);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0);
        (await RunStateAsync(world)).ShouldBe("queued");
    }

    [Fact]
    public async Task A_Report_And_An_Ingest_Batch_Racing_One_Unit_Of_Room_Commit_Exactly_One()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var reportArtifact = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 1m);

        // The events route's side of the race: a run of the same org and project, its planned execution and the one
        // producer slot its token is bound to, so its batch is an ordinary accepted insert until the counter answers.
        // The seeded run is the org's first project — this world's only one — and the ingest suite proves the run
        // state it is seeded in accepts batches, so the race needs no state of its own.
        var ingestRun = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        await IngestTestWorld.SeedSlotsAsync(
            _fixture, world.Database, world.Org, ingestRun.RunId, 1, IngestTestWorld.Jti);
        var ingestHost = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database),
            world.Org,
            world.Project,
            IngestTestWorld.Writer(projectScope: world.Project));
        await using var __ = ingestHost;
        using var ingestClient = ingestHost.CreateClient();
        var batch = IngestQaRequests.Batch(
            IngestQaRequests.Event(Guid.CreateVersion7(), ingestRun.Executions[0].ExecutionKey, 1));

        var reportTask = ParseAsync(world, reportArtifact);
        var ingestTask = IngestQaRequests.PostEventsAsync(ingestClient, ingestRun.RunId, batch);
        await Task.WhenAll(reportTask, ingestTask);

        var reportOutcome = await reportTask;
        var ingestAnswer = await ingestTask;
        // Each side must be answering the quota question, not failing for a reason of its own.
        reportOutcome.Status.ShouldBeOneOf(ParseRawReportOutcome.Parsed, ParseRawReportOutcome.QuotaBlocked);
        ingestAnswer.Status.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.TooManyRequests);

        var reportCommitted = reportOutcome.Status == ParseRawReportOutcome.Parsed;
        var ingestCommitted = ingestAnswer.Status == HttpStatusCode.OK;

        (reportCommitted ^ ingestCommitted).ShouldBeTrue(
            $"exactly one batch may take the last unit: report {reportOutcome.Status}, ingest {ingestAnswer.Status}");

        var counter = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        counter.Used.ShouldBe(1m, "the loser charged nothing and the limit is never exceeded");
        counter.Used.ShouldBeLessThanOrEqualTo(counter.Limit);
        (await OrgCountAsync(world, "event")).ShouldBe(1);
        (await OrgCountAsync(world, "event_batch")).ShouldBe(1);
        (await OrgCountAsync(world, "raw_report")).ShouldBe(reportCommitted ? 1 : 0);
    }

    /// <summary>Swaps the host's quota gate for a wrapper around the real one, resolved from the same scope.</summary>
    private static void ReplaceQuotaGate(IServiceCollection services, Func<IQuotaGate, IQuotaGate> wrap)
    {
        var registration = services.Last(descriptor => descriptor.ServiceType == typeof(IQuotaGate));
        var implementation = registration.ImplementationType
            ?? throw new InvalidOperationException("the host's IQuotaGate registration must name its implementation");

        services.Remove(registration);
        services.AddScoped<IQuotaGate>(provider =>
            wrap((IQuotaGate)ActivatorUtilities.CreateInstance(provider, implementation)));
    }

    /// <summary>Binds the host's clock, so the billing month and the deferral ceiling are the case's own.</summary>
    private static void UseClock(IServiceCollection services, TimeProvider clock)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton(clock);
    }

    /// <summary>The same attempt, against a handler whose lines a test can read.</summary>
    private static async Task<ParseRawReportOutcome> ParseAsync(
        World world,
        Guid artifactId,
        ILogger<ParseRawReportHandler> logger) =>
        await CreateHandler(world, logger).ExecuteAsync(
            new ParseRawReportPayload(world.RunId, artifactId, ProducerKind),
            world.Org,
            Token);

    private Task<long> OrgCountAsync(World world, string table) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.{table} where org_id = '{world.Org:D}'");

    [Fact]
    public async Task A_Deferred_Report_The_Run_Closes_On_Is_Reported_As_Blocked_Then_Closed()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);

        // The queue's own trace of the earlier attempt, keyed and worded exactly as the upload and the consumer write it.
        // The text carries an apostrophe ("handler's"), so it is escaped for the literal before it goes into the statement.
        var persistedError = TaskDeferredException.PersistedError.Replace("'", "''", StringComparison.Ordinal);
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.task (id, org_id, queue, task_type, payload, idempotency_key, state, last_error) values "
            + $"('{Guid.CreateVersion7():D}', '{world.Org:D}', 'tasks', '{EvidenceTaskTypes.ReportParse}', "
            + $"'{{\"run_id\":\"{world.RunId:D}\",\"artifact_id\":\"{artifactId:D}\"}}'::jsonb, "
            + $"'evidence:artifact:{artifactId:D}:{EvidenceTaskTypes.ReportParse}', 'retry', "
            + $"'{persistedError}')");
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set state = 'complete' where id = '{world.RunId:D}'");

        var log = new CapturedLog();
        var outcome = await ParseAsync(world, artifactId, log);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldNotBeNullOrEmpty("the outcome record keeps the admission's own code word");
        log.Lines.ShouldContain(
            line => line.Contains("quota_blocked_then_closed", StringComparison.Ordinal),
            "a batch the period refused and the run then closed is a loss, not an ordinary late report");
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_Real_Consumer_Deferral_Then_A_Closed_Run_Reports_Blocked_Then_Closed()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 1m, limit: 1m);
        var queue = "report-quota-closed";
        var taskId = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, $$"""
            insert into public.task (id, org_id, queue, task_type, payload, idempotency_key)
            values ('{{taskId:D}}', '{{world.Org:D}}', '{{queue}}', '{{EvidenceTaskTypes.ReportParse}}',
                '{"run_id":"{{world.RunId:D}}","artifact_id":"{{artifact:D}}","producer_kind":"pytest-junitxml"}'::jsonb,
                'evidence:artifact:{{artifact:D}}:{{EvidenceTaskTypes.ReportParse}}')
            """);
        await using var worker = StartReportWorker(world);
        var consumer = worker.GetRequiredService<TaskConsumer>();

        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskStateAsync(world, taskId)).ShouldBe("retry");
        (await _fixture.ScalarAsync<string>(
                world.Database, $"select last_error from public.task where id = '{taskId:D}'"))
            .ShouldBe(TaskDeferredException.PersistedError, "the probe compares the text the consumer really writes");

        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set state = 'complete' where id = '{world.RunId:D}'");
        var log = new CapturedLog();
        var outcome = await ParseAsync(world, artifact, log);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        log.Lines.ShouldContain(line => line.Contains("quota_blocked_then_closed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Run_Closed_Skip_Without_A_Deferral_Keeps_Its_Own_Code_Word()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 100m);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set state = 'complete' where id = '{world.RunId:D}'");

        var log = new CapturedLog();
        var outcome = await ParseAsync(world, artifactId, log);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        log.Lines.ShouldNotContain(line => line.Contains("quota_blocked_then_closed", StringComparison.Ordinal));
        log.Lines.ShouldContain(line => line.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The handler's structured lines, so a test can read the code word the skip path chose.</summary>
    internal sealed class CapturedLog : ILogger<ParseRawReportHandler>
    {
        private readonly List<string> _lines = [];

        /// <summary>Every formatted line, in arrival order.</summary>
        internal IReadOnlyList<string> Lines => _lines;

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _lines.Add(formatter(state, exception));
    }
}
