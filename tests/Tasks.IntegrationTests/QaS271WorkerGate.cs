using System.Collections.Concurrent;
using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class QaS271WorkerGate(PostgresTasksFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_GateSwitchControlsAdmission_AndOnlyDisabledNoticeWarns(bool enabled)
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        await TaskSql.ExecuteAsync(owner, """
            insert into public.organization (id, name, slug) values (@org, 'quota', 'quota');
            insert into public.project (id, org_id, name, slug) values (@project, @org, 'quota', 'quota');
            insert into public.quota_counter (id, org_id, project_id, period, metric, used, quota_limit)
            values (uuidv7(), @org, @project, to_char(now() at time zone 'UTC', 'YYYY-MM'), 'results', 1, 1)
            """, ("org", org), ("project", project));
        var logger = new NoticeProvider();
        var builder = WorkerHost.CreateBuilder(
        [
            $"--EVIDGATE:Database:ConnectionString={fixture.AppConnectionString(database)}",
            $"--EVIDGATE:Tasks:WorkerConnectionString={fixture.WorkerConnectionString(database)}",
            "--EVIDGATE:Runs:Cancel:SoftStopDelay=00:00:30",
            "--EVIDGATE:Runs:Cancel:KillDelay=00:02:00",
            $"--EVIDGATE:Usage:QuotaGate:Enabled={enabled.ToString(CultureInfo.InvariantCulture)}",
        ]);
        builder.Logging.ClearProviders().AddProvider(logger);
        using var host = builder.Build();
        var notice = host.Services.GetServices<IHostedService>().Single(service =>
            service.GetType().Name == "QuotaGateStartupNotice");
        await notice.StartAsync(TestContext.Current.CancellationToken);
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(enabled ? LogLevel.Information : LogLevel.Warning);
        entry.Id.ShouldBe(enabled ? 2475 : 2476);
        logger.Entries.Count(line => line.Level == LogLevel.Warning).ShouldBe(enabled ? 0 : 1);
        if (!enabled)
        {
            entry.Message.ShouldContain("Api");
            entry.Message.ShouldContain("Worker");
        }

        await using var services = host.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var gate = provider.GetRequiredService<IQuotaGate>();
        var answer = await gate.CheckAsync(
            new QuotaCheckRequest(UsageMetric.Results, Period: null, project, 1m, IncludeTenantScope: true),
            TestContext.Current.CancellationToken);
        answer.Decision.ShouldBe(enabled ? QuotaDecision.Block : QuotaDecision.Ok);
        if (enabled)
        {
            answer.Period.ShouldNotBeNull("the gate names the month it decided in");
        }
    }

    private sealed class NoticeProvider : ILoggerProvider
    {
        internal ConcurrentQueue<(LogLevel Level, int Id, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new NoticeLogger(Entries);

        public void Dispose()
        {
        }
    }

    private sealed class NoticeLogger(ConcurrentQueue<(LogLevel Level, int Id, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id is 2475 or 2476)
            {
                entries.Enqueue((logLevel, eventId.Id, formatter(state, exception)));
            }
        }
    }
}
