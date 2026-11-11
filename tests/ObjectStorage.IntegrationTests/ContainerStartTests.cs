using System.Diagnostics;
using System.Net;
using Docker.DotNet;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>Pure tests of the shared container start: a fake container, no Docker.</summary>
public sealed class ContainerStartTests
{
    private static readonly ContainerStartOptions Fast = new(
        MaxAttempts: 3,
        AttemptTimeout: TimeSpan.FromSeconds(5),
        Budget: TimeSpan.FromSeconds(30),
        Delay: TimeSpan.FromMilliseconds(1),
        DisposeTimeout: TimeSpan.FromSeconds(5));

    private sealed class Fake
    {
        public bool Disposed { get; set; }
    }

    private sealed class Harness
    {
        public List<Fake> Created { get; } = [];

        public List<string> Log { get; } = [];

        public Task<Fake> RunAsync(
            Func<int, CancellationToken, Task> start,
            ContainerStartOptions? options = null,
            Func<Fake, ValueTask>? dispose = null,
            CancellationToken? cancellationToken = null) =>
            ContainerStart.StartAsync(
                () =>
                {
                    var fake = new Fake();
                    Created.Add(fake);
                    return fake;
                },
                (fake, token) => start(Created.IndexOf(fake) + 1, token),
                dispose ?? (fake =>
                {
                    fake.Disposed = true;
                    return ValueTask.CompletedTask;
                }),
                options ?? Fast,
                Log.Add,
                cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private static TaskCanceledException DockerTimeout() =>
        new(
            "The operation was canceled.",
            new HttpRequestException("The requested failed, see inner exception for details.", new OperationCanceledException()));

    [Fact]
    public async Task ATimedOutDockerCall_IsRetried_AndTheHalfCreatedContainerIsDisposed()
    {
        var harness = new Harness();

        var started = await harness.RunAsync((attempt, _) => attempt == 1 ? Task.FromException(DockerTimeout()) : Task.CompletedTask);

        harness.Created.Count.ShouldBe(2);
        harness.Created[0].Disposed.ShouldBeTrue();
        started.ShouldBeSameAs(harness.Created[1]);
        started.Disposed.ShouldBeFalse();
        harness.Log.ShouldHaveSingleItem().ShouldContain("attempt 1/3");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ADockerServerError_IsRetried(HttpStatusCode status)
    {
        var harness = new Harness();

        await harness.RunAsync((attempt, _) =>
            attempt == 1 ? Task.FromException(new DockerApiException(status, "daemon busy")) : Task.CompletedTask);

        harness.Created.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task ADockerClientError_IsNotRetried_AndTheContainerIsStillDisposed(HttpStatusCode status)
    {
        var harness = new Harness();

        var failure = await Should.ThrowAsync<DockerApiException>(() =>
            harness.RunAsync((_, _) => Task.FromException(new DockerApiException(status, "no such image"))));

        failure.StatusCode.ShouldBe(status);
        harness.Created.Count.ShouldBe(1);
        harness.Created[0].Disposed.ShouldBeTrue();
        harness.Log.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnrelatedFailure_IsNotRetried()
    {
        var harness = new Harness();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            harness.RunAsync((_, _) => Task.FromException(new InvalidOperationException("bad configuration"))));

        harness.Created.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFatalLinkInTheChain_WinsOverATransientOne()
    {
        var harness = new Harness();
        var wrapped = new HttpRequestException("wrapped", new DockerApiException(HttpStatusCode.NotFound, "no such image"));

        await Should.ThrowAsync<HttpRequestException>(() => harness.RunAsync((_, _) => Task.FromException(wrapped)));

        harness.Created.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EveryAttemptIsSpentOnAPersistentTransientFailure_ThenTheLastErrorSurfaces()
    {
        var harness = new Harness();

        await Should.ThrowAsync<TaskCanceledException>(() =>
            harness.RunAsync((_, _) => Task.FromException(DockerTimeout())));

        harness.Created.Count.ShouldBe(3);
        harness.Created.ShouldAllBe(fake => fake.Disposed);
        harness.Log.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheBudgetBoundsTheRetries_EvenWhenTheAttemptCapIsHigh()
    {
        var harness = new Harness();
        var options = Fast with
        {
            MaxAttempts = 1000,
            AttemptTimeout = TimeSpan.FromMilliseconds(40),
            Budget = TimeSpan.FromMilliseconds(400),
        };
        var clock = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            harness.RunAsync((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token), options));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        harness.Created.Count.ShouldBeInRange(2, 50);
        harness.Created.ShouldAllBe(fake => fake.Disposed);
    }

    [Fact]
    public async Task ACallerCancellation_IsNeverRetried()
    {
        var harness = new Harness();
        using var cancellation = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => harness.RunAsync(
            async (_, token) =>
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
            cancellationToken: cancellation.Token));

        harness.Created.Count.ShouldBe(1);
        harness.Created[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task AStalledDispose_IsBounded_AndDoesNotBlockTheRetry()
    {
        var harness = new Harness();
        var options = Fast with { DisposeTimeout = TimeSpan.FromMilliseconds(50) };
        var stalled = new TaskCompletionSource();

        var started = await harness.RunAsync(
            (attempt, _) => attempt == 1 ? Task.FromException(DockerTimeout()) : Task.CompletedTask,
            options,
            dispose: _ => new ValueTask(stalled.Task));

        started.ShouldBeSameAs(harness.Created[1]);
        harness.Log.ShouldContain(line => line.Contains("disposing the failed container did not finish", StringComparison.Ordinal));
    }
}
