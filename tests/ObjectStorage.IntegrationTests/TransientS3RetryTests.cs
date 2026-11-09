using System.Net;
using Amazon.S3;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>Pure tests of the shared first-write retry: no container, no network.</summary>
public sealed class TransientS3RetryTests
{
    private static readonly TransientS3RetryOptions Fast = new(
        Budget: TimeSpan.FromSeconds(30),
        AttemptTimeout: TimeSpan.FromSeconds(5),
        Delay: TimeSpan.FromMilliseconds(1),
        MaxDelay: TimeSpan.FromMilliseconds(2),
        MaxAttempts: 4);

    private static AmazonS3Exception Answer(HttpStatusCode status) =>
        new("answer", Amazon.Runtime.ErrorType.Unknown, "Code", "req", status);

    [Fact]
    public async Task SucceedsOnTheSecondAttempt_AfterATransientAnswer()
    {
        var calls = 0;

        await TransientS3Retry.RunAsync(
            _ =>
            {
                calls++;
                return calls == 1 ? Task.FromException(Answer(HttpStatusCode.InternalServerError)) : Task.CompletedTask;
            },
            Fast);

        calls.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ATransientAnswer_IsRetriedUpToTheCap_ThenRethrownWithItsCause(HttpStatusCode status)
    {
        var calls = 0;

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => TransientS3Retry.RunAsync(
            _ =>
            {
                calls++;
                return Task.FromException(Answer(status));
            },
            Fast,
            () => Task.FromResult("log tail marker")));

        calls.ShouldBe(Fast.MaxAttempts);
        failure.InnerException.ShouldBeOfType<AmazonS3Exception>().StatusCode.ShouldBe(status);
        failure.Message.ShouldContain("log tail marker");
    }

    [Fact]
    public async Task ATransportFailure_IsRetried()
    {
        var calls = 0;

        await TransientS3Retry.RunAsync(
            _ =>
            {
                calls++;
                return calls < 3 ? Task.FromException(new HttpRequestException("reset")) : Task.CompletedTask;
            },
            Fast);

        calls.ShouldBe(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A4xxAnswer_IsRethrownAtOnce_WithoutRetry(HttpStatusCode status)
    {
        var calls = 0;

        var failure = await Should.ThrowAsync<AmazonS3Exception>(() => TransientS3Retry.RunAsync(
            _ =>
            {
                calls++;
                return Task.FromException(Answer(status));
            },
            Fast));

        calls.ShouldBe(1);
        failure.StatusCode.ShouldBe(status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AStatusBearingTransportFailure_In4xx_IsRethrownAtOnce(HttpStatusCode status)
    {
        var calls = 0;

        await Should.ThrowAsync<HttpRequestException>(() => TransientS3Retry.RunAsync(
            _ =>
            {
                calls++;
                return Task.FromException(new HttpRequestException("answer", null, status));
            },
            Fast));

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task AStatusBearingTransportFailure_In5xx_IsRetried()
    {
        var calls = 0;

        await TransientS3Retry.RunAsync(
            _ =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException(new HttpRequestException("answer", null, HttpStatusCode.ServiceUnavailable))
                    : Task.CompletedTask;
            },
            Fast);

        calls.ShouldBe(2);
    }

    [Fact]
    public async Task ASpentBudget_StopsBeforeTheCap_WithoutSleepingPastIt()
    {
        var options = Fast with { Budget = TimeSpan.FromMilliseconds(300), Delay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30), MaxAttempts = 100 };
        var started = DateTime.UtcNow;

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => TransientS3Retry.RunAsync(
            _ => Task.FromException(Answer(HttpStatusCode.InternalServerError)),
            options));

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        failure.InnerException.ShouldBeOfType<AmazonS3Exception>();
    }

    [Fact]
    public async Task AZeroBudget_RunsNoAttempt()
    {
        var calls = 0;

        await Should.ThrowAsync<InvalidOperationException>(() => TransientS3Retry.RunAsync(
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            },
            Fast with { Budget = TimeSpan.Zero }));

        calls.ShouldBe(0);
    }

    [Fact]
    public async Task AStepCancellation_ThatIsNotTheAttemptTimeout_IsNotRetriedAway()
    {
        var calls = 0;

        await Should.ThrowAsync<TaskCanceledException>(() => TransientS3Retry.RunAsync(
            _ =>
            {
                calls++;
                return Task.FromCanceled(new CancellationToken(canceled: true));
            },
            Fast));

        calls.ShouldBe(1);
    }
}
