using System.Net;
using Docker.DotNet;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>Pure tests of the Ryuk reaper start: a fake reaper and a fake Docker, no containers.</summary>
public sealed class ContainerStartReaperTests
{
    private const string Own = "testcontainers-ryuk-0b1c2d3e-0000-4000-8000-000000000001";

    private static readonly ContainerStartOptions Fast = new(
        MaxAttempts: 3,
        AttemptTimeout: TimeSpan.FromSeconds(5),
        Budget: TimeSpan.FromSeconds(30),
        Delay: TimeSpan.FromMilliseconds(1),
        DisposeTimeout: TimeSpan.FromSeconds(5));

    // Models dockerd: a create the client gave up on still lands later, and a create under an existing
    // name answers 409 until that container is removed.
    private sealed class FakeDocker
    {
        private bool _lateCreatePending;

        public bool RyukExists { get; private set; }

        public bool Started { get; private set; }

        public int Removed { get; private set; }

        public int Conflicts { get; private set; }

        public List<string> Log { get; } = [];

        public Task StartReaper(int attempt, CancellationToken token)
        {
            if (_lateCreatePending)
            {
                RyukExists = true;
                _lateCreatePending = false;
            }

            if (RyukExists)
            {
                Conflicts++;
                throw new DockerApiException(HttpStatusCode.Conflict, $"Conflict. The container name \"/{Own}\" is already in use by container \"abc\".");
            }

            if (attempt == 1)
            {
                _lateCreatePending = true;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
            }

            RyukExists = true;
            Started = true;
            return Task.CompletedTask;
        }

        public Task Remove(CancellationToken token)
        {
            Removed++;
            RyukExists = false;
            return Task.CompletedTask;
        }
    }

    private static Task Run(FakeDocker docker, Func<int, CancellationToken, Task> start, ContainerStartOptions? options = null)
    {
        var attempt = 0;
        return ContainerStart.EnsureResourceReaperAsync(
            Own,
            () => !docker.Started,
            token => start(++attempt, token),
            docker.Remove,
            options ?? Fast,
            docker.Log.Add);
    }

    [Fact]
    public async Task Timed_out_create_then_conflict_on_own_name_removes_it_and_leaves_one_reaper()
    {
        var docker = new FakeDocker();

        await Run(docker, docker.StartReaper);

        docker.Started.ShouldBeTrue();
        docker.RyukExists.ShouldBeTrue();
        docker.Conflicts.ShouldBe(1);
        docker.Removed.ShouldBe(2);
        docker.Log.ShouldContain(line => line.Contains("reaper attempt 1/3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Conflict_on_another_name_is_fatal_and_removes_nothing()
    {
        var docker = new FakeDocker();
        var foreign = new DockerApiException(HttpStatusCode.Conflict, "Conflict. The container name \"/testcontainers-ryuk-other\" is already in use");

        var failure = await Should.ThrowAsync<DockerApiException>(() => Run(docker, (_, _) => Task.FromException(foreign)));

        failure.ShouldBeSameAs(foreign);
        docker.Removed.ShouldBe(0);
    }

    [Fact]
    public async Task Persistent_conflict_stops_at_the_attempt_ceiling()
    {
        var docker = new FakeDocker();
        var calls = 0;

        await Should.ThrowAsync<DockerApiException>(() => Run(docker, (_, _) =>
        {
            calls++;
            return Task.FromException(new DockerApiException(HttpStatusCode.Conflict, $"The container name \"/{Own}\" is already in use"));
        }));

        calls.ShouldBe(Fast.MaxAttempts);
        docker.Removed.ShouldBe(Fast.MaxAttempts - 1);
    }

    [Fact]
    public async Task Budget_ends_the_retries()
    {
        var docker = new FakeDocker();
        var calls = 0;
        var tight = Fast with { Budget = TimeSpan.FromMilliseconds(50), Delay = TimeSpan.FromMilliseconds(60) };

        await Should.ThrowAsync<TaskCanceledException>(() => Run(docker, (_, _) =>
        {
            calls++;
            return Task.FromException(new TaskCanceledException("timeout"));
        }, tight));

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Cleanup_that_spends_the_budget_surfaces_the_docker_failure()
    {
        var docker = new FakeDocker();
        var tight = Fast with { Budget = TimeSpan.FromMilliseconds(300), Delay = TimeSpan.FromMilliseconds(1) };
        var failure = new TaskCanceledException("timeout");

        await Should.ThrowAsync<TaskCanceledException>(() => ContainerStart.EnsureResourceReaperAsync(
            Own,
            () => true,
            async _ =>
            {
                await Task.Yield();
                throw failure;
            },
            async token => await Task.Delay(TimeSpan.FromMilliseconds(400), token),
            tight,
            docker.Log.Add));
    }

    [Fact]
    public async Task Already_running_reaper_is_not_started_again()
    {
        var calls = 0;

        await ContainerStart.EnsureResourceReaperAsync(
            Own,
            () => false,
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            Fast,
            _ => { },
            TestContext.Current.CancellationToken);

        calls.ShouldBe(0);
    }

    [Fact]
    public void Name_match_needs_the_whole_container_name()
    {
        var conflict = new DockerApiException(HttpStatusCode.Conflict, $"The container name \"/{Own}\" is already in use");

        ContainerStart.IsNameConflict(conflict, Own).ShouldBeTrue();
        ContainerStart.IsNameConflict(conflict, Own[..^1]).ShouldBeFalse();
        ContainerStart.IsNameConflict(new DockerApiException(HttpStatusCode.NotFound, Own), Own).ShouldBeFalse();
    }

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task Stalled_dispose_is_logged_and_left_for_ryuk()
    {
        var log = new List<string>();
        var never = new TaskCompletionSource();

        await ContainerStart.DisposeAsync(new object(), _ => new ValueTask(never.Task), () => true, Short, log.Add);

        log.ShouldHaveSingleItem().ShouldContain("[container-stop]");
        log[0].ShouldContain("left for Ryuk");
        never.SetResult();
    }

    [Fact]
    public async Task Docker_timeout_from_dispose_is_logged_and_left_for_ryuk()
    {
        var log = new List<string>();

        await ContainerStart.DisposeAsync(new object(), _ => ValueTask.FromException(new HttpRequestException("reset")), () => true, Short, log.Add);

        log.ShouldHaveSingleItem().ShouldContain("left for Ryuk");
    }

    [Fact]
    public async Task Non_transient_dispose_failure_is_rethrown()
    {
        var failure = new DockerApiException(HttpStatusCode.NotFound, "no such container");

        var thrown = await Should.ThrowAsync<DockerApiException>(() => ContainerStart.DisposeAsync(
            new object(), _ => ValueTask.FromException(failure), () => true, Short, _ => { }));

        thrown.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task Transient_dispose_failure_is_rethrown_when_ryuk_is_not_running()
    {
        await Should.ThrowAsync<HttpRequestException>(() => ContainerStart.DisposeAsync(
            new object(), _ => ValueTask.FromException(new HttpRequestException("reset")), () => false, Short, _ => { }));

        var never = new TaskCompletionSource();
        await Should.ThrowAsync<TimeoutException>(() => ContainerStart.DisposeAsync(
            new object(), _ => new ValueTask(never.Task), () => false, Short, _ => { }));
        never.SetResult();
    }
}
