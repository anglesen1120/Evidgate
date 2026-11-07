using System.Net;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class GetRunDetailFieldsTests
{
    [Fact]
    public async Task GetRun_ForeignOrgRelease_CannotBeBoundOrDisclosed()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await using var foreign = await RunCreationTests.StartAsync(_fixture, slug: "foreign-release");
        var ownRelease = await CreateReleaseAsync(world, "own-release", "web");
        var runId = await CreateBoundRunAsync(world, ownRelease);
        var foreignRelease = await CreateReleaseAsync(foreign, "foreign-release", "web");
        var foreignRun = await CreateBoundRunAsync(foreign, foreignRelease);

        var foreignControl = await GetAsync(foreign, foreignRun);
        foreignControl.Status.ShouldBe(HttpStatusCode.OK, foreignControl.ErrorMessage);
        Releases(foreignControl).ShouldHaveSingleItem().ReleaseId.ShouldBe(foreignRelease);

        var failure = await Should.ThrowAsync<PostgresException>(() => SeedBindingAsync(
            world, foreignRelease, runId, "web", LaterBoundAt, Guid.CreateVersion7()));
        failure.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);

        var own = await GetAsync(world, runId);
        own.Status.ShouldBe(HttpStatusCode.OK, own.ErrorMessage);
        Releases(own).ShouldHaveSingleItem().ReleaseId.ShouldBe(ownRelease);
        own.Body.GetRawText().ShouldNotContain(foreignRelease.ToString());

        var denied = await GetAsync(world, foreignRun);
        denied.Status.ShouldBe(HttpStatusCode.NotFound);
        denied.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task GetRun_CanceledRun_CountsLateEventsWithoutReopeningOrAttemptRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await CreateReleaseAsync(world, "canceled-release", "web");
        var runId = await CreateBoundRunAsync(world, releaseId);
        var canceled = await CancelRunTestData.CancelAsync(world, runId);
        canceled.Status.ShouldBe(HttpStatusCode.Accepted, canceled.ErrorMessage);
        await SeedEventsAsync(world, runId, flagged: 2, unflagged: 1);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("canceled");
        answer.Body.GetProperty("after_cancel_events").GetInt32().ShouldBe(2);
        Releases(answer).ShouldHaveSingleItem().ReleaseId.ShouldBe(releaseId);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.case_attempt where run_id = '{runId:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task GetRun_RunReadWithoutDecisionRead_PreservesNonemptyReleaseAndLateEventMembers()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await CreateReleaseAsync(world, "reader-release", "web");
        var runId = await CreateBoundRunAsync(world, releaseId);
        await SeedEventsAsync(world, runId, flagged: 1, unflagged: 0);
        var decisionControl = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/runs/{runId}/decision", world.Admin);
        decisionControl.Status.ShouldBe(HttpStatusCode.OK, decisionControl.ErrorMessage);
        await GrantRunReadOnlyAsync(world);

        var answer = await GetAsync(world, runId);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Releases(answer).ShouldHaveSingleItem().ReleaseId.ShouldBe(releaseId);
        answer.Body.GetProperty("after_cancel_events").GetInt32().ShouldBe(1);

        var decision = await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId}/decision", world.Admin);
        decision.Status.ShouldBe(HttpStatusCode.Forbidden);
        decision.ErrorCode.ShouldBe("forbidden");
    }
}
