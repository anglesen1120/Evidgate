using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class GetRunDetailFieldsTests
{
    [Fact]
    public async Task GetRun_HistoryBoundaryDescendingClockTimes_ServesSequenceOrder()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        await _fixture.ExecuteAsync(world.Database, $"""
            insert into public.run_state_event (id, org_id, run_id, seq, from_state, to_state, occurred_at)
            values (uuidv7(), '{world.Org:D}', '{runId:D}', 2, 'queued', 'running', '2026-11-07T10:00:00Z'),
                   (uuidv7(), '{world.Org:D}', '{runId:D}', 3, 'running', 'collecting', '2026-11-07T09:00:00Z');
            """);

        var answer = await GetAsync(world, runId);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var served = StateHistory(answer);
        served.Count.ShouldBe(2);
        served[0].FromState.ShouldBe("queued");
        served[0].ToState.ShouldBe("running");
        served[1].FromState.ShouldBe("running");
        served[1].ToState.ShouldBe("collecting");
        served[0].OccurredAt.ShouldBeGreaterThan(served[1].OccurredAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    public async Task GetRun_HistoryBoundaryAtAndAcrossLimit_ServesFirst64Transitions(int count)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        await _fixture.ExecuteAsync(world.Database, $"""
            insert into public.run_state_event (id, org_id, run_id, seq, from_state, to_state, occurred_at)
            select uuidv7(), '{world.Org:D}', '{runId:D}', n + 1,
                   case when n = 1 then 'queued' when n % 2 = 0 then 'running' else 'collecting' end,
                   case when n % 2 = 0 then 'collecting' else 'running' end,
                   '2026-11-07T00:00:00Z'::timestamptz + n * interval '1 second'
              from generate_series(1, {count}) as n;
            """);

        var answer = await GetAsync(world, runId);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var served = StateHistory(answer);
        served.Count.ShouldBe(Math.Min(count, 64));
        if (count > 0)
        {
            served[0].FromState.ShouldBe("queued");
            served[^1].OccurredAt.ShouldBe(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(Math.Min(count, 64)));
        }

        (await _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(count + 1L);
    }

    [Fact]
    public async Task GetRun_HistoryBoundaryCanceledTimestamp_MatchesStoredUtcTimestamp()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        var answer = await GetAsync(world, runId);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var served = StateHistory(answer);
        served.ShouldHaveSingleItem();
        var stored = await _fixture.ScalarAsync<DateTime>(world.Database,
            $"select occurred_at from public.run_state_event where run_id = '{runId:D}' and seq = 2");
        stored.Kind.ShouldBe(DateTimeKind.Utc);
        served[0].OccurredAt.ShouldBe(new DateTimeOffset(stored));
    }
}
