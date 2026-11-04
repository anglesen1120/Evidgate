// 05.4.1-T1 AC2/§8: the terminal no-op and the repeat/concurrent answers of the cancel route.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class CancelRunEndpointTests
{
    /// <summary>
    /// AC2: a cancel on a run that already reached a terminal state answers the same empty <c>202</c> and writes
    /// nothing — no request row, no transition, no version bump — whichever terminal state it is.
    /// </summary>
    [Theory]
    [InlineData("complete")]
    [InlineData("incomplete")]
    [InlineData("canceled")]
    public async Task CancelRun_AlreadyTerminal_NoOp202(string terminal)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CancelRunTestData.RunInStateAsync(_fixture, world, terminal);

        var version = await CancelRunTestData.VersionAsync(_fixture, world, runId);
        var outbox = await CancelRunTestData.OutboxTargetsAsync(_fixture, world, runId);

        var answer = await CancelRunTestData.CancelAsync(world, runId, reason: "late cancel");

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        answer.Body.EnumerateObject().ShouldBeEmpty();
        (await CancelRunTestData.StateAsync(_fixture, world, runId))
            .ShouldBe(terminal, "a terminal run stays in its terminal state");
        (await CancelRunTestData.VersionAsync(_fixture, world, runId))
            .ShouldBe(version, "a no-op writes nothing, so it cannot bump the version");
        (await CancelRunTestData.OutboxTargetsAsync(_fixture, world, runId)).ShouldBe(outbox);
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId))
            .ShouldBe(0, "a terminal run never gets a cancel request row");
    }

    /// <summary>
    /// AC2/§8 idempotency: three accepted cancels of one non-terminal run leave exactly one request row and one
    /// applied transition — the unique <c>(org_id, run_id)</c> key is what makes the repeat a no-op, so distinct
    /// idempotency keys are used on purpose (the row, not the key cache, is the arbiter).
    /// </summary>
    [Fact]
    public async Task CancelRun_Repeated_NoDuplicateRequestRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var answer = await CancelRunTestData.CancelAsync(world, runId, reason: $"attempt {attempt}");

            answer.Status.ShouldBe(HttpStatusCode.Accepted, $"attempt {attempt}: {answer.ErrorMessage}");
        }

        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(1);
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("canceled");
        (await CancelRunTestData.VersionAsync(_fixture, world, runId))
            .ShouldBe(1, "only the first cancel applied a transition");
        (await CancelRunTestData.OutboxTargetsAsync(_fixture, world, runId)).ShouldBe(["canceled"]);
    }

    /// <summary>
    /// §8 concurrency: six simultaneous cancels of one queued run all answer <c>202</c> and leave one row, one
    /// transition and no server error — the unique key and the transition's row lock decide the winner, never a
    /// check-then-insert race in the handler.
    /// </summary>
    [Fact]
    public async Task CancelRun_Concurrent_OneRow_No500()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var answers = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(index => CancelRunTestData.CancelAsync(world, runId, reason: $"concurrent {index}")));

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        }

        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(1);
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("canceled");
        (await CancelRunTestData.VersionAsync(_fixture, world, runId))
            .ShouldBe(1, "one applied transition, whatever order the requests won in");
        (await CancelRunTestData.OutboxTargetsAsync(_fixture, world, runId)).ShouldBe(["canceled"]);
    }
}
