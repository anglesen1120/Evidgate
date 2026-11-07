using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class GetRunDetailFieldsTests
{
    [Fact]
    public async Task GetRun_ExactlyFiveHundredBindings_ServesEveryStoredRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        await SeedBulkBindingsAsync(world, runId, 500);
        var stored = await StoredReleasesAsync(world, runId);
        stored.Count.ShouldBe(500, "this boundary is exactly the limit, not one row beyond it");

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Releases(answer).Count.ShouldBe(500);
        Releases(answer).Select(Row).ShouldBe(stored);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetRun_UncommittedCancelAndBinding_SeesOnlyCommittedStateAndReleases(bool commit)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        var release = await SeedReleaseAsync(world, "detail-overlapping-cancel", world.Project);
        await using (var writer = await TenantScope.BeginAsync(
            _fixture.OwnerConnectionString(world.Database), world.Org, world.Project))
        {
            (await writer.ExecuteAsync(
                "update public.run set state = 'canceled' where org_id = @org_id and id = @run_id",
                ("org_id", world.Org), ("run_id", runId))).ShouldBe(1);
            (await writer.ExecuteAsync(
                "insert into public.release_run (id, org_id, release_id, run_id, component_key) "
                + "values (@id, @org_id, @release_id, @run_id, 'web')",
                ("id", Guid.CreateVersion7()), ("org_id", world.Org),
                ("release_id", release), ("run_id", runId))).ShouldBe(1);
            (await writer.ScalarAsync<string>(
                "select state from public.run where id = @run_id", ("run_id", runId))).ShouldBe("canceled");
            (await writer.ScalarAsync<long>(
                "select count(*) from public.release_run where run_id = @run_id", ("run_id", runId))).ShouldBe(1);

            // The HTTP reader uses a second connection while both changes remain uncommitted.
            var overlapping = await GetAsync(world, runId)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            overlapping.Status.ShouldBe(HttpStatusCode.OK, overlapping.ErrorMessage);
            overlapping.Text("state").ShouldBe("queued");
            Releases(overlapping).ShouldBeEmpty();
            overlapping.Body.GetProperty("state_history").GetArrayLength().ShouldBe(0);
            if (commit)
            {
                await writer.CommitAsync();
            }
        }

        var settled = await GetAsync(world, runId);
        settled.Status.ShouldBe(HttpStatusCode.OK, settled.ErrorMessage);
        settled.Text("state").ShouldBe(commit ? "canceled" : "queued");
        Releases(settled).Count.ShouldBe(commit ? 1 : 0);
        Releases(settled).Select(Row).ShouldBe(await StoredReleasesAsync(world, runId));
        var history = settled.Body.GetProperty("state_history");
        history.GetArrayLength().ShouldBe(commit ? 1 : 0);
        if (commit)
        {
            Releases(settled).ShouldHaveSingleItem().ReleaseId.ShouldBe(release);
            history[0].GetProperty("from_state").GetString().ShouldBe("queued");
            history[0].GetProperty("to_state").GetString().ShouldBe("canceled");
        }
    }

    [Fact]
    public async Task GetRun_OnlySiblingProjectBinding_HidesItUntilAnOwnProjectBindingExists()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling-only");
        var foreign = await SeedReleaseAsync(world, "detail-hidden-sibling", sibling);
        await SeedBindingAsync(world, foreign, runId, "web", TiedBoundAt, Guid.CreateVersion7());
        (await StoredReleasesAsync(world, runId)).ShouldHaveSingleItem();

        var hidden = await GetAsync(world, runId);

        hidden.Status.ShouldBe(HttpStatusCode.OK, hidden.ErrorMessage);
        Releases(hidden).ShouldBeEmpty();
        hidden.Body.GetRawText().ShouldNotContain(foreign.ToString());
        hidden.Body.GetRawText().ShouldNotContain("detail-hidden-sibling");

        var own = await SeedReleaseAsync(world, "detail-visible-own", world.Project);
        await SeedBindingAsync(world, own, runId, "web", LaterBoundAt, Guid.CreateVersion7());
        var control = await GetAsync(world, runId);
        control.Status.ShouldBe(HttpStatusCode.OK, control.ErrorMessage);
        Releases(control).ShouldHaveSingleItem().ReleaseId.ShouldBe(own);
        (await StoredReleasesAsync(world, runId)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetRun_LegacyRunWithoutBindings_KeepsUnknownHistoryAndEmptyReleasesDistinct()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: owned.Name);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        await RemoveStateHistoryAsync(world, runId);
        (await _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(0);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("queued");
        answer.Body.GetProperty("state_history").ValueKind.ShouldBe(JsonValueKind.Null);
        answer.Body.GetProperty("releases").ValueKind.ShouldBe(JsonValueKind.Array);
        Releases(answer).ShouldBeEmpty();
    }
}
