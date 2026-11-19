using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// Each fail-closed clause of the gate on its own (store, timeout and unrequested cancellation): a store that throws the named exception type is answered with the
/// shared 503, and removing the clause lets that exact type escape the gate, so the case fails.
/// </summary>
public sealed class RateLimitGateFaultClauseTests
{
    [Fact]
    public async Task Acquire_AStoreThrowingNpgsqlException_AnswersUnavailable() =>
        await AssertUnavailableAsync(new NpgsqlException("store refused the command"));

    [Fact]
    public async Task Acquire_AStoreThrowingTimeoutException_AnswersUnavailable() =>
        await AssertUnavailableAsync(new TimeoutException("store did not answer"));

    [Fact]
    public async Task Acquire_ACommandTimeoutCancellationTheRequestDidNotAskFor_AnswersUnavailable() =>
        await AssertUnavailableAsync(new TaskCanceledException("command timed out"));

    [Fact]
    public async Task Acquire_ACancellationTheRequestAskedFor_Propagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var limiter = new FaultingLimiter(new OperationCanceledException(cancelled.Token), failAfterCalls: 0);

        await Should.ThrowAsync<OperationCanceledException>(() => RateLimitGate.AcquireAsync(
            new DefaultHttpContext(), limiter, RateSurface.Login, "k", 1, "refused", cancelled.Token).AsTask());
    }

    [Fact]
    public async Task Acquire_AStoreThrowingAnotherException_IsNotSwallowed() =>
        await Should.ThrowAsync<InvalidOperationException>(() => AcquireAsync(new InvalidOperationException("bug")).AsTask());

    [Fact]
    public async Task Acquire_ATwoUnitStoreCallFailingWithTimeoutException_AnswersUnavailable()
    {
        var limiter = new FaultingLimiter(new TimeoutException("weighted attempt"), failAfterCalls: 0);
        var context = new DefaultHttpContext();

        var refused = await RateLimitGate.AcquireAsync(
            context, limiter, RateSurface.Login, "k", 2, "refused", TestContext.Current.CancellationToken);

        refused.ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.Unavailable);
        limiter.Calls.ShouldBe(1);
        limiter.LastCost.ShouldBe(2);

        // Control: the same weighted attempt through a store that answers is one call, so the case above fails
        // for the fault and not for a cost the gate cannot spend.
        var answering = new FaultingLimiter(new TimeoutException("never thrown"), failAfterCalls: 1);
        (await RateLimitGate.AcquireAsync(
            new DefaultHttpContext(), answering, RateSurface.Login, "k", 2, "refused", TestContext.Current.CancellationToken))
            .ShouldBeNull();
        answering.Calls.ShouldBe(1);
        answering.LastCost.ShouldBe(2);
    }

    private static async Task AssertUnavailableAsync(Exception fault)
    {
        var context = new DefaultHttpContext();

        var refused = await AcquireAsync(fault, context);

        refused.ShouldNotBeNull("a count that could not be taken never admits");
        refused.Code.ShouldBe(ApiErrorCode.Unavailable);
        refused.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().ShouldBe("1");
    }

    private static ValueTask<Error?> AcquireAsync(Exception fault, DefaultHttpContext? context = null) =>
        RateLimitGate.AcquireAsync(
            context ?? new DefaultHttpContext(),
            new FaultingLimiter(fault, failAfterCalls: 0),
            RateSurface.Login,
            "k",
            1,
            "refused",
            TestContext.Current.CancellationToken);

    private sealed class FaultingLimiter(Exception fault, int failAfterCalls)
        : ExchangeRateLimiter(NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused"), Options.Create(new RateLimitOptions()))
    {
        public int Calls { get; private set; }
        public int LastCost { get; private set; }

        public override ValueTask<RateDecision> AcquireAsync(
            RateSurface surface, string key, int cost, CancellationToken cancellationToken)
        {
            Calls++;
            LastCost = cost;
            if (Calls > failAfterCalls)
            {
                throw fault;
            }

            return ValueTask.FromResult(new RateDecision(true, 0));
        }
    }
}
