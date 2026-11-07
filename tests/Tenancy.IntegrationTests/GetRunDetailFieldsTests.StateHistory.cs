// The run detail's state history, through the real host and a real PostgreSQL: the rows the write trigger appended are
// the only source, so a body that reports a transition the table does not hold — or hides one it does — fails here.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class GetRunDetailFieldsTests
{
    /// <summary>The bound the contract states for <c>state_history</c>; the read must apply exactly this.</summary>
    private const int ServedHistoryBound = 64;

    /// <summary>
    /// A run that was just created serves the member as an empty array — known, and not yet transitioned — while the
    /// table holds the creation marker alone: sequence 1, no from state, into the state the insert carried.
    /// </summary>
    [Fact]
    public async Task GetRun_FreshRun_ServesAnEmptyStateHistoryAndWritesTheCreationMarker()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("state_history").ValueKind.ShouldBe(JsonValueKind.Array);
        answer.Body.GetProperty("state_history").GetArrayLength().ShouldBe(0);
        answer.Body.GetRawText().ShouldContain("\"state_history\":[]");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"""
            select count(*) from public.run_state_event
             where run_id = '{runId:D}' and seq = 1 and from_state is null and to_state = 'queued'
            """)).ShouldBe(1, "the creation row is the marker, written by the trigger on insert");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(
            1,
            "a run that never transitioned holds the marker and nothing else");
    }

    /// <summary>
    /// A canceled run serves the one transition the cancel wrote, with the time the row carries, and never the creation
    /// row — the marker is not a transition, so a served item always has a from state.
    /// </summary>
    [Fact]
    public async Task GetRun_CanceledRun_ServesTheRecordedTransitionWithoutTheCreationRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("canceled");
        var served = StateHistory(answer);
        served.ShouldHaveSingleItem();
        served[0].FromState.ShouldBe("queued");
        served[0].ToState.ShouldBe("canceled");
        served[0].OccurredAt.ShouldBe(await _fixture.ScalarAsync<DateTimeOffset>(
            world.Database,
            $"select occurred_at from public.run_state_event where run_id = '{runId:D}' and seq = 2"));
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(
            2,
            "the marker plus the one transition, and only the transition is served");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"""
            select count(*) from public.run_state_event
             where run_id = '{runId:D}' and from_state is null
            """)).ShouldBe(1, "exactly one row has no from state and it is the marker");
    }

    /// <summary>
    /// The order is the row's sequence, not the recorded time: a run whose second transitioned row carries the later
    /// clock still serves them in sequence order, so a clock that steps backwards cannot reorder a history.
    /// </summary>
    [Fact]
    public async Task GetRun_StateHistory_FollowsTheRowSequenceNotTheRecordedTime()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.run_state_event (org_id, run_id, seq, from_state, to_state, occurred_at)
            values ('{world.Org:D}', '{runId:D}', 2, 'queued', 'running', '2026-11-07T10:00:00Z'),
                   ('{world.Org:D}', '{runId:D}', 3, 'running', 'collecting', '2026-11-07T09:00:00Z');
            """);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var served = StateHistory(answer);
        served.Select(row => $"{row.FromState}->{row.ToState}").ShouldBe(
            ["queued->running", "running->collecting"],
            "sequence order, which here runs against the recorded time");
        served[0].OccurredAt.ShouldBeGreaterThan(
            served[1].OccurredAt,
            "the times descend, so anything but the row sequence would have reversed the list");
    }

    /// <summary>
    /// A run with no creation marker can only be one created before the table existed: its history is unknown, so the
    /// member is null — never an empty array, which would claim the run never transitioned. The control below is a run
    /// of the same database whose marker survived.
    /// </summary>
    [Fact]
    public async Task GetRun_RunWithoutTheCreationMarker_ServesNullHistory()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        // The marker is removed as the superuser with the table's write guards disabled — the only way a stored
        // history row can disappear — and they are re-enabled immediately, so the state is the row's, not the trigger's.
        await _fixture.ExecuteAsync(world.Database, "alter table public.run_state_event disable trigger user");
        await _fixture.ExecuteAsync(
            world.Database, $"delete from public.run_state_event where run_id = '{runId:D}'");
        await _fixture.ExecuteAsync(world.Database, "alter table public.run_state_event enable trigger user");

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("state_history").ValueKind.ShouldBe(JsonValueKind.Null);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(0);

        var known = await CreateQueuedRunAsync(world);
        var control = await GetAsync(world, known);
        control.Status.ShouldBe(HttpStatusCode.OK, control.ErrorMessage);
        control.Body.GetProperty("state_history").ValueKind.ShouldBe(
            JsonValueKind.Array,
            "an empty array and null are different answers, and the marker is what separates them");
    }

    /// <summary>
    /// The read applies the contract's bound: a run with more rows than the bound serves exactly
    /// <see cref="ServedHistoryBound"/> items, the first ones in sequence order, and the newest rows stay in the table.
    /// </summary>
    [Fact]
    public async Task GetRun_StateHistory_IsBoundedToTheContractLimit()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.run_state_event (org_id, run_id, seq, from_state, to_state, occurred_at)
            select '{world.Org:D}', '{runId:D}', n, 'queued', 'running',
                   '2026-11-07T00:00:00Z'::timestamptz + (n || ' seconds')::interval
              from generate_series(2, 71) as n;
            """);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var served = StateHistory(answer);
        served.Count.ShouldBe(ServedHistoryBound);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(
            71,
            "the table keeps every row; the bound is the read's");
        served[^1].OccurredAt.ShouldBe(
            await _fixture.ScalarAsync<DateTimeOffset>(
                world.Database,
                $"select occurred_at from public.run_state_event where run_id = '{runId:D}' and seq = 65"),
            "the read takes the first 64 rows in sequence order, so the last served row is sequence 65");
    }

    /// <summary>
    /// Another tenant's read of the run is the one <c>404 not_found</c> — the body carries no history member at all —
    /// and a credential scoped to a sibling project is the same answer, so neither learns that the transitions exist.
    /// </summary>
    [Fact]
    public async Task GetRun_StateHistory_IsNotServedToAnotherTenantOrProject()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await using var rival = await RunCreationTests.StartAsync(_fixture, slug: "history-rival");
        var runId = await CreateQueuedRunAsync(world);
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        var stored = await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_state_event where run_id = '{runId:D}'");
        stored.ShouldBe(2, "the rows exist, so their absence below is the read's scoping and not an empty table");

        var crossTenant = await GetAsync(
            world, runId, TenancyApiFactory.Resolved(user: rival.User, orgHeader: rival.Org));
        crossTenant.Status.ShouldBe(HttpStatusCode.NotFound);
        crossTenant.ErrorCode.ShouldBe("not_found");
        crossTenant.Body.GetRawText().ShouldNotContain("state_history");

        await using var scoped = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(world.Database));
        using var client = scoped.CreateClient();
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var siblingScope = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/runs/{runId}",
            TenancyApiFactory.Caller(
                org: world.Org, project: sibling, serviceScope: sibling, servicePermissions: ["run:read"]));

        siblingScope.Status.ShouldBe(HttpStatusCode.NotFound);
        siblingScope.ErrorCode.ShouldBe(crossTenant.ErrorCode);
    }

    /// <summary>One served <c>state_history</c> item: the three required members and nothing else.</summary>
    private sealed record ServedTransition(string FromState, string ToState, DateTimeOffset OccurredAt);

    /// <summary>The served <c>state_history</c> array, in the order the read reported it.</summary>
    private static List<ServedTransition> StateHistory(ApiAnswer answer) =>
    [
        .. answer.Body.GetProperty("state_history").EnumerateArray().Select(item =>
        {
            item.EnumerateObject().Select(member => member.Name)
                .ShouldBe(["from_state", "to_state", "occurred_at"], ignoreOrder: true);

            return new ServedTransition(
                item.GetProperty("from_state").GetString()!,
                item.GetProperty("to_state").GetString()!,
                item.GetProperty("occurred_at").GetDateTimeOffset());
        }),
    ];

    /// <summary>Creates one run through the served route, so the trigger writes its creation marker.</summary>
    private static async Task<Guid> CreateQueuedRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("run_id");
    }
}
