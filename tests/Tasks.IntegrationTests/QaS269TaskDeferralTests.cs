using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class QaS269TaskDeferralTests(PostgresTasksFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task TaskConsumer_RepeatedDeferralsAtLastAttempt_StillCompletes(int priorAttempts, int maxAttempts)
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        const string queue = "deferral-boundary";
        const string key = "eventually-admitted";
        await TaskSql.SeedTaskAsync(owner, queue, TaskHandlerTypes.Recording,
            payload: """{"value":"eventually-admitted"}""", idempotencyKey: key,
            attempts: priorAttempts, maxAttempts: maxAttempts);
        var alerts = new RecordingTaskAlertSink();
        await using var host = TasksTestHost.Start(fixture, database,
            services: services => services.AddSingleton<ITaskAlertSink>(alerts));
        host.Recorder.Failure = new TaskDeferredException(TimeSpan.FromHours(1));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await host.Consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
            (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = @key", ("key", key)))
                .ShouldBe("retry");
            (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = @key", ("key", key)))
                .ShouldBe(priorAttempts);
            (await host.Consumer.PollOnceAsync(queue, Token)).ShouldBe(0);
            alerts.Alerts.ShouldBeEmpty();
            await TaskSql.ExecuteAsync(owner,
                "update public.task set available_at = now() - interval '1 second' where idempotency_key = @key", ("key", key));
        }

        host.Recorder.Failure = null;
        (await host.Consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = @key", ("key", key)))
            .ShouldBe("done");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = @key", ("key", key)))
            .ShouldBe(maxAttempts);
        host.Recorder.Runs[key].ShouldBe(4);
        alerts.Alerts.ShouldBeEmpty();
        (await host.Consumer.PollOnceAsync(queue, Token)).ShouldBe(0);
    }

    [Fact]
    public async Task TaskConsumer_OrdinaryFailureAtLastAttempt_DeadLettersAndAlerts()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        const string queue = "failure-boundary";
        const string key = "ordinary-capped";
        await TaskSql.SeedTaskAsync(owner, queue, TaskHandlerTypes.Recording,
            payload: """{"value":"ordinary-capped"}""", idempotencyKey: key, maxAttempts: 1);
        var alerts = new RecordingTaskAlertSink();
        await using var host = TasksTestHost.Start(fixture, database,
            services: services => services.AddSingleton<ITaskAlertSink>(alerts));
        host.Recorder.Failure = new InvalidOperationException("ordinary failure");

        (await host.Consumer.PollOnceAsync(queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = @key", ("key", key)))
            .ShouldBe("dead_letter");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = @key", ("key", key)))
            .ShouldBe(1);
        alerts.Alerts.ShouldHaveSingleItem();
        (await host.Consumer.PollOnceAsync(queue, Token)).ShouldBe(0);
    }
}
