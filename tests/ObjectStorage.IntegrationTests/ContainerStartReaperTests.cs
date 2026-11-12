using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Evidgate.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>Pure tests of the Ryuk reaper start: a fake reaper and a fake Docker, no containers.</summary>
public sealed class ContainerStartReaperTests
{
    private const string Own = "testcontainers-ryuk-0b1c2d3e-0000-4000-8000-000000000001";

    private const string FakeId = "d38e704b97b9a1c2e3f4a5b6c7d8e9f00112233445566778899aabbccddeeff0";

    // Verbatim shape of dockerd's answer to a create under a taken name.
    private static string Conflict409(string id) =>
        $"Conflict. The container name \"/{Own}\" is already in use by container \"{id}\". "
        + "You have to remove (or rename) that container to be able to reuse that name.";

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
                throw new DockerApiException(HttpStatusCode.Conflict, Conflict409(FakeId));
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

        public Task Remove(string? conflictingId, CancellationToken token)
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
            async (_, token) => await Task.Delay(TimeSpan.FromMilliseconds(400), token),
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
            (_, _) => Task.CompletedTask,
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

    [Fact]
    public void Conflict_text_yields_the_quoted_container_id_also_when_json_escaped()
    {
        var plain = new DockerApiException(HttpStatusCode.Conflict, Conflict409(FakeId));
        var escaped = new DockerApiException(HttpStatusCode.Conflict, Conflict409(FakeId).Replace("\"", "\\\"", StringComparison.Ordinal));

        ContainerStart.ConflictingContainerId(plain, Own).ShouldBe(FakeId);
        ContainerStart.ConflictingContainerId(escaped, Own).ShouldBe(FakeId);
        ContainerStart.ConflictingContainerId(new TaskCanceledException("x"), Own).ShouldBeNull();
    }

    [Fact]
    public async Task Conflict_hands_the_quoted_id_to_the_removal()
    {
        var ids = new List<string?>();
        var calls = 0;

        await ContainerStart.EnsureResourceReaperAsync(
            Own,
            () => calls < 2,
            _ =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException(new DockerApiException(HttpStatusCode.Conflict, Conflict409(FakeId)))
                    : Task.CompletedTask;
            },
            (id, _) =>
            {
                ids.Add(id);
                return Task.CompletedTask;
            },
            Fast,
            _ => { },
            TestContext.Current.CancellationToken);

        ids.ShouldBe([FakeId]);
    }

    private static DockerContainerNotFoundException NotFound() => new(HttpStatusCode.NotFound, "No such container");

    [Fact]
    public async Task Remove_by_name_answering_404_still_removes_by_the_quoted_id_and_waits_for_the_name()
    {
        var containers = Substitute.For<IContainerOperations>();
        containers.RemoveContainerAsync(Own, Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(NotFound()));
        containers.RemoveContainerAsync(FakeId, Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var inspects = 0;
        containers.InspectContainerAsync(Own, Arg.Any<CancellationToken>()).Returns(_ =>
            ++inspects <= 2
                ? Task.FromResult(new ContainerInspectResponse { ID = FakeId })
                : Task.FromException<ContainerInspectResponse>(NotFound()));
        var log = new List<string>();

        await ContainerStart.RemoveReaperContainerAsync(
            containers, Own, FakeId, log.Add, TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(1));

        inspects.ShouldBe(3);
        log.ShouldContain(line => line.Contains("not found by remove (404)", StringComparison.Ordinal));
        log.ShouldContain(line => line.Contains("removed stale reaper container by id", StringComparison.Ordinal));
        log.Count(line => line.Contains("still held", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public async Task Conflict_keeps_removing_while_dockerd_still_registers_the_container()
    {
        var containers = Substitute.For<IContainerOperations>();
        var removes = 0;
        containers.RemoveContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++removes <= 4 ? Task.FromException(NotFound()) : Task.CompletedTask);
        containers.InspectContainerAsync(Own, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ContainerInspectResponse>(NotFound()));

        await ContainerStart.RemoveReaperContainerAsync(
            containers, Own, FakeId, _ => { }, TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(1));

        removes.ShouldBe(6);
    }

    [Fact]
    public async Task Name_found_after_both_removes_answered_404_is_removed_again_without_a_quoted_id()
    {
        var containers = Substitute.For<IContainerOperations>();
        var removes = 0;
        containers.RemoveContainerAsync(Own, Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++removes == 1 ? Task.FromException(NotFound()) : Task.CompletedTask);
        containers.InspectContainerAsync(Own, Arg.Any<CancellationToken>()).Returns(_ =>
            removes < 2
                ? Task.FromResult(new ContainerInspectResponse { ID = FakeId })
                : Task.FromException<ContainerInspectResponse>(NotFound()));

        await ContainerStart.RemoveReaperContainerAsync(
            containers, Own, null, _ => { }, TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(1));

        removes.ShouldBe(2);
    }

    [Fact]
    public async Task Removal_that_landed_is_remembered_when_later_removes_answer_404()
    {
        var containers = Substitute.For<IContainerOperations>();
        var removes = 0;
        containers.RemoveContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++removes <= 2 ? Task.CompletedTask : Task.FromException(NotFound()));
        var inspects = 0;
        containers.InspectContainerAsync(Own, Arg.Any<CancellationToken>()).Returns(_ =>
            ++inspects == 1
                ? Task.FromResult(new ContainerInspectResponse { ID = FakeId })
                : Task.FromException<ContainerInspectResponse>(NotFound()));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        await ContainerStart.RemoveReaperContainerAsync(
            containers, Own, FakeId, _ => { }, cts.Token, TimeSpan.FromMilliseconds(1));

        inspects.ShouldBe(2);
    }

    [Fact]
    public async Task Caller_cancellation_ends_the_conflict_cleanup_before_the_dispose_timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var calls = 0;
        var started = DateTime.UtcNow;

        await Should.ThrowAsync<OperationCanceledException>(() => ContainerStart.EnsureResourceReaperAsync(
            Own,
            () => true,
            _ => ++calls == 1
                ? Task.FromException(new DockerApiException(HttpStatusCode.Conflict, Conflict409(FakeId)))
                : Task.CompletedTask,
            async (_, token) => await Task.Delay(TimeSpan.FromSeconds(20), token),
            Fast,
            _ => { },
            cts.Token));

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Name_that_stays_taken_ends_with_the_callers_token()
    {
        var containers = Substitute.For<IContainerOperations>();
        containers.RemoveContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        containers.InspectContainerAsync(Own, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ContainerInspectResponse { ID = FakeId }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => ContainerStart.RemoveReaperContainerAsync(
            containers, Own, null, _ => { }, cts.Token, TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public async Task Remove_without_a_quoted_id_uses_the_name_only()
    {
        var containers = Substitute.For<IContainerOperations>();
        containers.RemoveContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        containers.InspectContainerAsync(Own, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ContainerInspectResponse>(NotFound()));

        await ContainerStart.RemoveReaperContainerAsync(
            containers, Own, null, _ => { }, TestContext.Current.CancellationToken);

        await containers.Received(1).RemoveContainerAsync(
            Own, Arg.Is<ContainerRemoveParameters>(p => p.Force == true), Arg.Any<CancellationToken>());
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
    public async Task Docker_client_timeout_from_dispose_is_logged_and_left_for_ryuk()
    {
        var log = new List<string>();

        await ContainerStart.DisposeAsync(
            new object(),
            async _ =>
            {
                await Task.Yield();
                throw new TaskCanceledException("The operation was canceled.", new HttpRequestException("timeout"));
            },
            () => true,
            Short,
            log.Add);

        log.ShouldHaveSingleItem().ShouldContain("left for Ryuk");
    }

    [Fact]
    public async Task Synchronous_transient_dispose_failure_is_left_for_ryuk()
    {
        var log = new List<string>();

        await ContainerStart.DisposeAsync(
            new object(), _ => throw new HttpRequestException("reset"), () => true, Short, log.Add);

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
