// The events route's quota admission over the real host and a real PostgreSQL: one gate call per batch (never
// per event), a block answered as 429 quota_exceeded carrying the gate's own Retry-After and writing nothing,
// a committed batch that survives a later block, 50 racing batches that never pass the limit, a resend that
// reserves nothing, the operator switch, and a foreign tenant's counter left untouched.
#pragma warning disable CA1861
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The transport half of the quota rule: the ingest route asks <see cref="IQuotaGate"/> once for a whole batch,
/// refuses the batch the period has no room for, and never charges a resend. The gate's own arithmetic is
/// covered by <see cref="UsageQuotaGateTests"/>; this suite covers what the route does with its answer.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class IngestQuotaTests
{
    private const decimal WarnRatio = 0.8m;

    private readonly PostgresTenancyFixture _fixture;

    public IngestQuotaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ---------------------------------------------------------------- one call per batch (T53)

    [Fact]
    public async Task IngestEvents_CallsQuotaGateOncePerBatch()
    {
        var spy = new QuotaGateSpy();
        await using var world = await StartIngestAsync(spy);
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 0m, limit: 10m);

        var answer = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L), (Guid.CreateVersion7(), 3L));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(3);

        // One batch, one question: the quantity is the batch's event count, so a per-event implementation
        // would show three calls (or three increments) instead of one.
        spy.Calls.ShouldBe(1);
        var request = spy.Requests.ShouldHaveSingleItem();
        request.Metric.ShouldBe(UsageMetric.Results);
        request.ProjectId.ShouldBe(world.Project);
        request.Period.ShouldBe(Period(world));
        request.Quantity.ShouldBe(3m);

        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(3m);
    }

    // ---------------------------------------------------------------- block: 429 quota_exceeded + Retry-After

    [Fact]
    public async Task IngestEvents_QuotaBlock_Returns429QuotaExceededWithRetryAfter()
    {
        await using var world = await StartIngestAsync(running: false);
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        // One unit of room for a batch of two: the batch is refused as a whole, never partly admitted.
        await SeedAsync(counters, world, used: 9m, limit: 10m);

        var answer = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L));

        answer.Status.ShouldBe(HttpStatusCode.TooManyRequests, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("quota_exceeded");
        answer.Body.GetProperty("error").GetProperty("retryable").GetBoolean().ShouldBeTrue();

        var retryAfter = RetryAfter(answer);
        var expected = SecondsUntilNextPeriod(world);
        retryAfter.ShouldBeGreaterThanOrEqualTo(1);
        retryAfter.ShouldBeInRange(expected - 1, expected + 2);

        // The operator raise route behind the gate is not published to a tenant: the refusal says the code
        // and the wait, nothing else.
        answer.Body.ToString().ShouldNotContain("/internal/operator");

        // A refusal is complete before the batch writes anything, and it never touches the run.
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);
        (await RunStateAsync(world)).ShouldBe("queued");
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(9m);
    }

    [Fact]
    public async Task IngestEvents_QuotaBlock_CountsTheDenialMetricAndOneBatchRejection()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 1m, limit: 1m);

        using var quotaListener = IngestTelemetryCapture.QuotaDeniedBatches(out var kinds);
        using var rejectionListener = IngestTelemetryCapture.RejectedBatches(out var reasons);

        var answer = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L));

        answer.Status.ShouldBe(HttpStatusCode.TooManyRequests, answer.ErrorMessage);
        kinds.ShouldContain("quota_exceeded");
        reasons.ShouldContain("quota_exceeded");
    }

    // ---------------------------------------------------------------- a committed batch completes

    [Fact]
    public async Task IngestEvents_CommittedBatch_CompletesAfterABlock()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 0m, limit: 2m);

        var committed = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L));
        committed.Status.ShouldBe(HttpStatusCode.OK, committed.ErrorMessage);

        var refused = await PostBatchAsync(world, (Guid.CreateVersion7(), 3L));
        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);

        // What was committed before the refusal stays committed: the accepted batch's rows, its metered fact
        // and the run the first batch started are exactly as the 200 left them.
        (await EventRowsAsync(world)).ShouldBe(2);
        (await BatchRowsAsync(world)).ShouldBe(1);
        (await world.CountAsync("outbox", $"org_id = '{world.Org:D}' and task_type = 'usage.batch_accepted'")).ShouldBe(1);
        (await RunStateAsync(world)).ShouldBe("running");
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(2m);
    }

    // ---------------------------------------------------------------- a resend reserves nothing

    [Fact]
    public async Task IngestEvents_ReplayedBatch_DoesNotChargeQuota()
    {
        var spy = new QuotaGateSpy();
        await using var world = await StartIngestAsync(spy);
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 0m, limit: 10m);

        var json = IngestQaRequests.Json(StepBatch(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L)));

        var first = await IngestQaRequests.PostAsync(world.Client, world.RunId, new StringContent(json, Encoding.UTF8, "application/json"));
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        spy.Calls.ShouldBe(1);

        var replay = await IngestQaRequests.PostAsync(world.Client, world.RunId, new StringContent(json, Encoding.UTF8, "application/json"));

        // The resend is answered with duplicate acks and metered nothing, so it must not spend quota either:
        // the batch is committed, the counter does not move and the gate is not asked a second time.
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Body.GetProperty("accepted").GetInt32().ShouldBe(0);
        replay.Body.GetProperty("duplicates").GetInt32().ShouldBe(2);
        spy.Calls.ShouldBe(1);

        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(2m);
        (await EventRowsAsync(world)).ShouldBe(2);
        (await world.CountAsync("outbox", $"org_id = '{world.Org:D}' and task_type = 'usage.batch_accepted'")).ShouldBe(1);
    }

    // ---------------------------------------------------------------- racing the last unit (AC5)

    [Fact]
    public async Task IngestEvents_ParallelBatches_NeverOvershootQuota()
    {
        const int batches = 50;
        const decimal limit = 20m;

        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 0m, limit: limit);

        var answers = await Task.WhenAll(Enumerable.Range(1, batches).Select(sequence =>
            PostBatchAsync(world, (Guid.CreateVersion7(), (long)sequence))));

        answers.ShouldAllBe(answer =>
            answer.Status == HttpStatusCode.OK || answer.Status == HttpStatusCode.TooManyRequests);

        // Exactly the reserved units are admitted, the rest are refused, and the counter never passes the limit.
        answers.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe((int)limit);
        answers.Count(answer => answer.Status == HttpStatusCode.TooManyRequests).ShouldBe(batches - (int)limit);
        answers.Where(answer => answer.Status == HttpStatusCode.TooManyRequests)
            .ShouldAllBe(answer => answer.ErrorCode == "quota_exceeded");

        var counter = (await ReadAsync(counters, world)).ShouldNotBeNull();
        counter.Used.ShouldBe(limit);
        counter.Used.ShouldBeLessThanOrEqualTo(counter.Limit);
        (await EventRowsAsync(world)).ShouldBe((int)limit);
        (await BatchRowsAsync(world)).ShouldBe((int)limit);
    }

    // ---------------------------------------------------------------- operator switch

    [Fact]
    public async Task IngestEvents_QuotaDisabled_PassesThrough()
    {
        await using var world = await StartIngestAsync(configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Usage:QuotaGate:Enabled"] = "false",
        });
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        // A scope with no room at all: only the switch can let the batch through.
        await SeedAsync(counters, world, used: 1m, limit: 1m);
        var before = (await ReadAsync(counters, world)).ShouldNotBeNull();

        var answer = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(2);

        // The switched-off gate answers Ok without reading or writing a counter.
        var after = (await ReadAsync(counters, world)).ShouldNotBeNull();
        after.Used.ShouldBe(before.Used);
        after.Version.ShouldBe(before.Version);
    }

    [Fact]
    public async Task IngestEvents_QuotaWarn_Proceeds()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        // 7 of 10 with a batch of two crosses the 80 % warn ratio and still fits: a warn is advice, and the
        // batch it warns about is admitted.
        await SeedAsync(counters, world, used: 7m, limit: 10m);

        var answer = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(9m);
    }

    // ---------------------------------------------------------------- tenancy

    [Fact]
    public async Task IngestEvents_ForeignOrgCounter_Untouched()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        await SeedAsync(counters, world, used: 0m, limit: 10m);
        var (otherOrg, otherProject) = await world.CreateOtherOrgAsync(Unique.Slug("quota-other"));
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), otherProject, Period(world), "results", 1m, 1m, WarnRatio, Org: otherOrg));
        var otherBefore = (await counters.CounterAsync(otherOrg, otherProject, Period(world))).ShouldNotBeNull();

        var answer = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(2m);

        var otherAfter = (await counters.CounterAsync(otherOrg, otherProject, Period(world))).ShouldNotBeNull();
        otherAfter.Used.ShouldBe(otherBefore.Used);
        otherAfter.Version.ShouldBe(otherBefore.Version);
    }

    // ---------------------------------------------------------------- fail closed

    [Fact]
    public async Task IngestEvents_GateThrows_RefusesTheBatch()
    {
        await using var world = await StartIngestAsync(services: services => services.AddSingleton<IQuotaGate>(new ThrowingQuotaGate()));
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 0m, limit: 10m);

        var answer = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L));

        // An admission the gate could not decide is refused: the host's envelope answers, and the batch is
        // never admitted unmetered.
        answer.Status.ShouldBe(HttpStatusCode.InternalServerError);
        answer.ErrorCode.ShouldBe("internal_error");
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(0m);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<IngestTestWorld> StartIngestAsync(
        QuotaGateSpy? spy = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null,
        bool running = true)
    {
        Action<IServiceCollection>? extra = services;
        if (spy is not null)
        {
            extra = collection =>
            {
                var registration = collection.Last(descriptor => descriptor.ServiceType == typeof(IQuotaGate));
                collection.Remove(registration);
                collection.AddScoped<IQuotaGate>(provider => new SpyQuotaGate(
                    (IQuotaGate)ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!),
                    spy));
            };
        }

        var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 1, configuration: configuration, services: extra);

        // The racing batches of this suite contend on the counter (the unit under test) and not on the
        // queued -> running transition, so a world that is not studying the transition starts running.
        if (running)
        {
            await world.SetRunStateAsync("running");
        }

        return world;
    }

    private static Task<Guid> SeedAsync(UsageQuotaWorld counters, IngestTestWorld world, decimal used, decimal limit) =>
        counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, Period(world), "results", used, limit, WarnRatio, Org: world.Org));

    private static Task<CounterState?> ReadAsync(UsageQuotaWorld counters, IngestTestWorld world) =>
        counters.CounterAsync(org: world.Org, project: world.Project, period: Period(world), metric: "results");

    private static string Period(IngestTestWorld world) =>
        world.Host.Services.GetRequiredService<TimeProvider>().GetUtcNow()
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static int SecondsUntilNextPeriod(IngestTestWorld world)
    {
        var now = world.Host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var next = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);

        return (int)Math.Ceiling((next - now).TotalSeconds);
    }

    /// <summary>One batch of <c>test.step</c> events (no attempt slot), one per given id and producer sequence.</summary>
    private static object StepBatch(IngestTestWorld world, params (Guid Id, long Sequence)[] events) =>
        IngestQaRequests.Batch(
            [.. events.Select(e => IngestQaRequests.Event(
                e.Id, IngestQaRequests.KeyOf(world, 0), e.Sequence, type: "test.step", status: null))]);

    private static Task<ApiAnswer> PostBatchAsync(IngestTestWorld world, params (Guid Id, long Sequence)[] events) =>
        IngestQaRequests.PostEventsAsync(world.Client, world.RunId, StepBatch(world, events));

    private static Task<long> EventRowsAsync(IngestTestWorld world) =>
        world.CountAsync("event", $"run_id = '{world.RunId:D}'");

    private static Task<long> BatchRowsAsync(IngestTestWorld world) =>
        world.CountAsync("event_batch", $"run_id = '{world.RunId:D}'");

    private static Task<string?> RunStateAsync(IngestTestWorld world) =>
        world.ScalarAsync<string?>($"select state from public.run where id = '{world.RunId:D}'");

    /// <summary>The integer <c>Retry-After</c> of a 429, parsed exactly as an RFC 9110 delay-seconds value.</summary>
    private static int RetryAfter(ApiAnswer answer)
    {
        answer.Headers.TryGetValues("Retry-After", out var values).ShouldBeTrue("a 429 must carry Retry-After");
        var raw = values!.ShouldHaveSingleItem();

        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            .ShouldBeTrue($"Retry-After must be integer seconds, was '{raw}'");

        return seconds;
    }
}

/// <summary>The gate questions one host asked, in arrival order.</summary>
internal sealed class QuotaGateSpy
{
    private int _calls;
    private readonly ConcurrentQueue<QuotaCheckRequest> _requests = new();

    /// <summary>How many <see cref="IQuotaGate.CheckAsync"/> calls the host made.</summary>
    internal int Calls => Volatile.Read(ref _calls);

    /// <summary>Every question, in arrival order.</summary>
    internal IReadOnlyList<QuotaCheckRequest> Requests => [.. _requests];

    internal void Record(QuotaCheckRequest request)
    {
        Interlocked.Increment(ref _calls);
        _requests.Enqueue(request);
    }
}

/// <summary>The registered gate, counted: the batch's one question is what the counter sees.</summary>
internal sealed class SpyQuotaGate(IQuotaGate inner, QuotaGateSpy spy) : IQuotaGate
{
    /// <inheritdoc />
    public Task<QuotaCheckResult> CheckAsync(QuotaCheckRequest request, CancellationToken cancellationToken)
    {
        spy.Record(request);

        return inner.CheckAsync(request, cancellationToken);
    }
}

/// <summary>A gate that cannot answer, so the route's fail-closed path is what the test sees.</summary>
internal sealed class ThrowingQuotaGate : IQuotaGate
{
    /// <inheritdoc />
    public Task<QuotaCheckResult> CheckAsync(QuotaCheckRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("the quota store is unavailable");
}
