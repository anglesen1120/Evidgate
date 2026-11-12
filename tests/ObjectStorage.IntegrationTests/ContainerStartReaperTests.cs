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

    // Models dockerd: the container named like the reaper exists once any create reached it, and a create
    // under an existing name answers 409 until that container is removed.
    private sealed class FakeDocker
    {
        public bool RyukExists { get; private set; }

        public bool Started { get; private set; }

        public int Removed { get; private set; }

        public List<string> Log { get; } = [];

        public Task StartReaper(int attempt, CancellationToken token)
        {
            if (RyukExists)
            {
                throw new DockerApiException(HttpStatusCode.Conflict, $"Conflict. The container name \"/{Own}\" is already in use by container \"abc\".");
            }

            RyukExists = true;
            if (attempt == 1)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
            }

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
        docker.Removed.ShouldBe(1);
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
}
