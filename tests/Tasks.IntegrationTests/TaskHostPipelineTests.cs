using System.Net;
using System.Net.Sockets;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Worker;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T1.2: the real Worker composition (<see cref="WorkerHost"/>, the same
/// services the entry point builds) consumes the queue — the queue plan derives
/// `maintenance` from the registered schedules, the consumer claims the seeded task,
/// dispatches it to its handler and marks it `done`, and the handler's side effect is
/// observable. The unit-level suites prove the mechanism; this one proves the wiring
/// that makes the mechanism run in the host.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskHostPipelineTests
{
    private readonly PostgresTasksFixture _fixture;

    public TaskHostPipelineTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Worker_Host_Consumes_The_Queue_And_Runs_The_Task()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        // The handler's observable side effect: a terminal row past the retention
        // window that only `task.sweep` deletes.
        await TaskSql.SeedTaskAsync(
            owner,
            "host-seed",
            TaskHandlerTypes.Recording,
            state: "done",
            idempotencyKey: "host-old-terminal",
            createdAt: DateTime.UtcNow.AddDays(-(EvidgateSweeps.TerminalRetentionDays + 30)));

        // …and the sweep task itself, on the queue the registered schedules declare.
        await TaskSql.SeedTaskAsync(
            owner,
            EvidgateSweeps.MaintenanceQueue,
            EvidgateSweeps.TaskSweepTaskType,
            payload: $$"""{"older_than_days":{{EvidgateSweeps.TerminalRetentionDays}}}""",
            idempotencyKey: "host-sweep");

        var host = await StartWorkerAsync(database);

        using (host)
        {
            var drained = await WaitUntilAsync(
                async () => await TaskSql.ScalarAsync<string>(
                    owner,
                    "select state from public.task where idempotency_key = 'host-sweep'") == "done",
                TimeSpan.FromSeconds(30));

            await host.StopAsync(Token);

            drained.ShouldBeTrue("the Worker host's consumer must claim and complete the queued task");

            (await TaskSql.ScalarAsync<long>(
                owner,
                "select count(*) from public.task where idempotency_key = 'host-old-terminal'"))
                .ShouldBe(0, "the handler ran: the retention sweep deleted the old terminal row");
        }
    }

    /// <summary>
    /// Starts the Worker on a free loopback health port. The port is probed, released
    /// and then bound by the host, so a parallel test assembly can take it in between;
    /// a bind collision retries on a fresh port instead of failing the suite.
    /// </summary>
    private async Task<IHost> StartWorkerAsync(string database)
    {
        for (var attempt = 1; ; attempt++)
        {
            var host = WorkerHost.CreateBuilder(
            [
                $"--EVIDGATE:Database:ConnectionString={_fixture.AppConnectionString(database)}",
                $"--EVIDGATE:Tasks:WorkerConnectionString={_fixture.WorkerConnectionString(database)}",
                "--EVIDGATE:Runs:Cancel:SoftStopDelay=00:00:30",
                "--EVIDGATE:Runs:Cancel:KillDelay=00:02:00",
                "--EVIDGATE:Worker:HealthAddress=127.0.0.1",
                $"--EVIDGATE:Worker:HealthPort={FreePort()}",
                "--EVIDGATE:Worker:ReadinessPollMilliseconds=100",
                "--EVIDGATE:Tasks:PollIntervalMilliseconds=50",
            ]).Build();

            try
            {
                await host.StartAsync(Token);
                return host;
            }
            catch (Exception exception) when (attempt < 3
                && exception.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                host.Dispose();
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50, Token);
        }

        return await condition();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
