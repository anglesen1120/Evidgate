// The burst budget of POST /v1/runs/{r}/events over the real host and a real PostgreSQL: a burst over the
// per-project, per-token limit is 429 rate_limited with an integer Retry-After and no write, header values
// cannot move a caller into another partition, and a caller without both identity values is refused before
// the budget is consulted. The partition arithmetic itself is unit-tested in IngestionRateLimiterTests,
// because each host owns its own limiter instance.
#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The rate-limit suite's HTTP half: <c>RateLimitTests.burst_returns_429_with_retry_after</c> (AC1) plus the
/// spoofing, no-write and fail-closed cases the same route must hold.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class RateLimitTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RateLimitTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>A window long enough that every request of one test falls inside it, so no test waits on time.</summary>
    private const int WindowSeconds = 3600;

    /// <summary>The options a world's host is built with: a budget small enough for one test to exhaust.</summary>
    private static Dictionary<string, string?> Limit(int permitLimit) => new(StringComparer.Ordinal)
    {
        ["EVIDGATE:Ingestion:RateLimit:PermitLimit"] = permitLimit.ToString(CultureInfo.InvariantCulture),
        ["EVIDGATE:Ingestion:RateLimit:WindowSeconds"] = WindowSeconds.ToString(CultureInfo.InvariantCulture),
    };

    // ---------------------------------------------------------------- §9 row: burst over rate → 429 + Retry-After

    [Fact]
    public async Task burst_returns_429_with_retry_after()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 5, configuration: Limit(permitLimit: 2));

        // Positive control: the two requests inside the budget are accepted and written.
        var first = await PostAsync(world, sequence: 1);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);

        var second = await PostAsync(world, sequence: 2);
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(2);

        // The third request of the same project and token is over the budget.
        var third = await PostAsync(world, sequence: 3);

        third.Status.ShouldBe(HttpStatusCode.TooManyRequests, third.ErrorMessage);
        third.ErrorCode.ShouldBe("rate_limited");
        var retryAfter = RetryAfter(third);
        retryAfter.ShouldBeGreaterThanOrEqualTo(1);
        retryAfter.ShouldBeLessThanOrEqualTo(WindowSeconds);

        // AC1: the refusal happens before any event write.
        (await EventRowsAsync(world)).ShouldBe(2);
        (await world.CountAsync("event_batch", $"run_id = '{world.RunId:D}'")).ShouldBe(2);
    }

    // ---------------------------------------------------------------- no header moves the partition

    [Fact]
    public async Task spoofed_headers_do_not_change_the_partition()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 5, configuration: Limit(permitLimit: 1));

        (await PostAsync(world, sequence: 1)).Status.ShouldBe(HttpStatusCode.OK);

        // The exhausted token cannot buy a fresh budget by naming another project, scope or token in headers.
        var spoofed = await PostAsync(world, sequence: 2, spoof: true);

        spoofed.Status.ShouldBe(HttpStatusCode.TooManyRequests, spoofed.ErrorMessage);
        spoofed.ErrorCode.ShouldBe("rate_limited");
        (await EventRowsAsync(world)).ShouldBe(1);
    }

    // ---------------------------------------------------------------- refusal writes nothing

    [Fact]
    public async Task refusal_writes_nothing()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 5, configuration: Limit(permitLimit: 1));

        (await PostAsync(world, sequence: 1)).Status.ShouldBe(HttpStatusCode.OK);

        var eventsBefore = await EventRowsAsync(world);
        var batchesBefore = await world.CountAsync("event_batch", $"run_id = '{world.RunId:D}'");
        var outboxBefore = await world.CountAsync("outbox");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var refused = await PostAsync(world, sequence: 2 + attempt);

            refused.Status.ShouldBe(HttpStatusCode.TooManyRequests);
            refused.ErrorCode.ShouldBe("rate_limited");
        }

        // Counted by the run's own key, so a row written into another run or org would still show up.
        (await EventRowsAsync(world)).ShouldBe(eventsBefore);
        (await world.CountAsync("event_batch", $"run_id = '{world.RunId:D}'")).ShouldBe(batchesBefore);
        (await world.CountAsync("outbox")).ShouldBe(outboxBefore);
    }

    // ---------------------------------------------------------------- fail closed without a partition

    [Fact]
    public async Task caller_without_a_project_scope_is_refused_and_writes_nothing()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);

        // A service token that is not scoped to a project has no budget to charge: the route refuses it
        // rather than sharing one partition with every other unscoped caller.
        await using var unscoped = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database),
            world.Org,
            world.Project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Event.Write], credentialId: IngestTestWorld.Jti));

        using var client = unscoped.CreateClient();

        var answer = await PostAsync(world, client, sequence: 1);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task member_principal_is_refused_and_writes_nothing()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);

        await using var member = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database),
            world.Org,
            world.Project,
            RequestPrincipal.Member(world.User));

        using var client = member.CreateClient();

        var answer = await PostAsync(world, client, sequence: 1);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<long> EventRowsAsync(IngestTestWorld world) =>
        world.CountAsync("event", $"run_id = '{world.RunId:D}'");

    private static Task<ApiAnswer> PostAsync(
        IngestTestWorld world,
        HttpClient? client,
        long sequence,
        bool spoof = false) =>
        IngestQaRequests.PostAsync(
            client ?? world.Client,
            world.RunId,
            IngestQaRequests.JsonBody(IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: sequence))),
            mutate: request =>
            {
                if (!spoof)
                {
                    return;
                }

                request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7");
                request.Headers.TryAddWithoutValidation("X-Project-Id", Guid.CreateVersion7().ToString("D"));
                request.Headers.TryAddWithoutValidation("X-Token-Jti", IngestTestWorld.OtherJti);
                request.Headers.TryAddWithoutValidation("X-Test-Service-Scope", Guid.CreateVersion7().ToString("D"));
            });

    private static Task<ApiAnswer> PostAsync(IngestTestWorld world, long sequence, bool spoof = false) =>
        PostAsync(world, client: null, sequence, spoof);

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
