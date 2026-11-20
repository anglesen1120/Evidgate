// `run.completed_at` against a real PostgreSQL: the collecting -> complete edge stamps it once, every role is refused a
// caller-chosen instant (update and insert), the app role cannot write the column at all, and a complete row without it
// is closed. The admission decisions built on it live in the reconcile and report suites; this class owns the column's
// shape and the window edge the shared predicate is measured from.
using System.Net;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class RunCompletedAtTests : IClassFixture<TenancyHostPool>
{
    /// <summary>The columns a run row needs, copied from a seeded run so the parent keys stay valid.</summary>
    private const string InsertRunSql =
        "insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, "
        + "state, collection_deadline_at, late_window_minutes) "
        + "select @id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, @state, "
        + "collection_deadline_at, late_window_minutes from public.run where id = @seed";

    /// <summary>The same insert with a caller-chosen completion instant, which no role may write.</summary>
    internal const string InsertRunWithInstantSql =
        "insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, "
        + "state, collection_deadline_at, late_window_minutes, completed_at) "
        + "select @id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, @state, "
        + "collection_deadline_at, late_window_minutes, now() from public.run where id = @seed";

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public RunCompletedAtTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    [Fact]
    public async Task CollectingToComplete_StampsCompletedAtOnce()
    {
        await using var world = await StartAsync();
        await world.SetRunStateAsync("collecting");
        (await CompletedAtAsync(world, world.RunId)).ShouldBeNull("collecting is not terminal");

        await using (var scope = await AppScopeAsync(world))
        {
            (await scope.ExecuteAsync($"update public.run set state = 'complete' where id = '{world.RunId:D}'")).ShouldBe(1);
            await scope.CommitAsync();
        }

        var stamped = await CompletedAtAsync(world, world.RunId);
        stamped.ShouldNotBeNull("the allowed edge stamps the completion instant");
        (await world.ScalarAsync<bool>(
            $"select completed_at <= now() and completed_at > now() - interval '1 minute' "
            + $"from public.run where id = '{world.RunId:D}'"))
            .ShouldBeTrue("the stamp is the transition's own now(), not a caller value");

        // A repeat of the terminal write is a no-op on the column: the state guard only runs on a state change, so the
        // value the first edge stamped is the value the row keeps.
        await using (var scope = await AppScopeAsync(world))
        {
            (await scope.ExecuteAsync($"update public.run set state = 'complete' where id = '{world.RunId:D}'")).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await CompletedAtAsync(world, world.RunId)).ShouldBe(stamped, "the completion instant is stamped once");
    }

    /// <summary>
    /// The window's own edge: one transaction completes the run, so the instant the guard stamps is that transaction's
    /// <c>now()</c>, and the shared predicate asked in the same transaction still admits. A later transaction is past it.
    /// </summary>
    [Fact]
    public async Task ZeroMinuteWindow_AtTheCompletionInstant_IsOpen_ThenClosedByALaterTransaction()
    {
        await using var world = await StartAsync();
        await world.SetRunStateAsync("collecting");
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set late_window_minutes = 0 where id = '{world.RunId:D}'");

        await using (var scope = await AppScopeAsync(world))
        {
            (await scope.ExecuteAsync($"update public.run set state = 'complete' where id = '{world.RunId:D}'"))
                .ShouldBe(1);
            (await scope.ScalarAsync<bool>(LateWindowSql(world)))
                .ShouldBeTrue("the completion instant plus zero is this transaction's own now(), so the edge is included");
            await scope.CommitAsync();
        }

        await using (var later = await AppScopeAsync(world))
        {
            (await later.ScalarAsync<bool>(LateWindowSql(world)))
                .ShouldBeFalse("a later transaction's clock is past a zero-minute window");
        }
    }

    [Fact]
    public async Task IncompleteAndCanceled_LeaveCompletedAtNull()
    {
        await using var world = await StartAsync();
        await world.SetRunStateAsync("incomplete");
        var canceled = await world.SeedOtherRunAsync(world.Org);
        await RunStateWalk.MoveAsync(_fixture, world.Database, canceled.RunId, "canceled");

        (await CompletedAtAsync(world, world.RunId)).ShouldBeNull("incomplete is not a completion");
        (await CompletedAtAsync(world, canceled.RunId)).ShouldBeNull("canceled is not a completion");
    }

    [Fact]
    public async Task OwnerUpdateToAnotherInstant_IsRefused()
    {
        await using var world = await StartAsync();
        await world.SetRunStateAsync("complete");
        var stamped = await CompletedAtAsync(world, world.RunId);
        stamped.ShouldNotBeNull("the case needs the instant it tries to move");

        var refused = await OwnerErrorAsync(
            world, $"update public.run set completed_at = now() - interval '1 hour' where id = '{world.RunId:D}'");

        refused.SqlState.ShouldBe("23514");
        refused.MessageText.ShouldStartWith("run_completed_at_immutable");
        (await CompletedAtAsync(world, world.RunId)).ShouldBe(stamped, "the refused update changed nothing");
    }

    [Fact]
    public async Task OwnerClearingCompletedAt_IsRefused()
    {
        await using var world = await StartAsync();
        await world.SetRunStateAsync("complete");

        var refused = await OwnerErrorAsync(
            world, $"update public.run set completed_at = null where id = '{world.RunId:D}'");

        refused.SqlState.ShouldBe("23514");
        refused.MessageText.ShouldStartWith("run_completed_at_immutable");
        (await CompletedAtAsync(world, world.RunId)).ShouldNotBeNull("a closed run never loses its anchor");
    }

    [Fact]
    public async Task OwnerSettingCompletedAtOnNonCompleteRun_IsRefused()
    {
        await using var world = await StartAsync();
        await world.SetRunStateAsync("collecting");

        var refused = await OwnerErrorAsync(
            world, $"update public.run set completed_at = now() where id = '{world.RunId:D}'");

        refused.SqlState.ShouldBe("23514");
        refused.MessageText.ShouldStartWith("run_completed_at_immutable");
        (await CompletedAtAsync(world, world.RunId)).ShouldBeNull();
    }

    [Fact]
    public async Task AppRoleUpdateOfCompletedAt_IsRefused()
    {
        await using var world = await StartAsync();
        await world.SetRunStateAsync("complete");
        var stamped = await CompletedAtAsync(world, world.RunId);

        await using var scope = await AppScopeAsync(world);
        (await scope.CaptureErrorAsync($"update public.run set completed_at = now() where id = '{world.RunId:D}'"))
            .SqlState.ShouldBe("42501", "no column grant covers completed_at");
        (await CompletedAtAsync(world, world.RunId)).ShouldBe(stamped);
    }

    [Fact]
    public async Task AppStateUpdate_CannotSupplyCompletedAt()
    {
        await using var world = await StartAsync();
        await world.SetRunStateAsync("collecting");

        await using (var refused = await AppScopeAsync(world))
        {
            (await refused.CaptureErrorAsync(
                $"update public.run set state = 'complete', completed_at = now() - interval '1 hour' "
                + $"where id = '{world.RunId:D}'")).SqlState.ShouldBe("42501", "the app role may not name the column");
        }

        await using (var scope = await AppScopeAsync(world))
        {
            (await scope.ExecuteAsync($"update public.run set state = 'complete' where id = '{world.RunId:D}'")).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await world.ScalarAsync<bool>(
            $"select completed_at > now() - interval '1 minute' from public.run where id = '{world.RunId:D}'"))
            .ShouldBeTrue("the trigger's own now() is the only value the app can obtain");
    }

    [Fact]
    public async Task InsertWithChosenCompletedAt_IsRefusedForEveryRole()
    {
        await using var world = await StartAsync();
        var seed = world.RunId;
        var appRow = Guid.CreateVersion7();

        await using (var app = await AppScopeAsync(world))
        {
            var refused = await app.CaptureErrorAsync(
                InsertRunWithInstantSql, ("id", appRow), ("seed", seed), ("state", "complete"));
            refused.SqlState.ShouldBe("23514");
            refused.MessageText.ShouldStartWith("run_completed_at_insert_refused");
        }

        await using (var owner = await OwnerScopeAsync(world))
        {
            var refused = await owner.CaptureErrorAsync(
                InsertRunWithInstantSql, ("id", Guid.CreateVersion7()), ("seed", seed), ("state", "complete"));
            refused.SqlState.ShouldBe("23514", "the insert guard runs for the owner too");
            refused.MessageText.ShouldStartWith("run_completed_at_insert_refused");
        }

        // The same insert without the column is allowed and leaves the row closed: no backfill, fail closed.
        var inserted = await InsertCompleteAsync(world);
        (await CompletedAtAsync(world, inserted)).ShouldBeNull("a complete row without an instant is closed");
        (await world.ScalarAsync<string>($"select state from public.run where id = '{inserted:D}'")).ShouldBe("complete");
        (await CompletedAtAsync(world, appRow)).ShouldBeNull("the refused insert wrote no row");
    }

    [Fact]
    public async Task ForeignOrgScope_CannotAcceptEventOnCompleteRun()
    {
        await using var world = await StartAsync();
        await world.SetRunStateAsync("complete");
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set late_window_minutes = 5 where id = '{world.RunId:D}'");
        await using var rival = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);

        var refused = await IngestQaRequests.PostEventsAsync(
            rival.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), world.Plan.Executions[0].ExecutionKey, sequence: 1)));

        refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
        (await world.CountAsync("event", $"run_id = '{world.RunId:D}'")).ShouldBe(0, "the foreign scope wrote nothing");
        (await rival.CountAsync("event", $"run_id = '{world.RunId:D}'")).ShouldBe(0);

        // Positive control: the same request from the owning tenant is admitted inside the window.
        var accepted = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), world.Plan.Executions[0].ExecutionKey, sequence: 1)));

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
    }

    /// <summary>A committed complete row without a completion instant: the shape no backfill leaves behind.</summary>
    internal static async Task<Guid> InsertCompleteAsync(IngestTestWorld world)
    {
        var id = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(world.Fixture.OwnerConnectionString(world.Database));

        (await scope.ExecuteAsync(InsertRunSql, ("id", id), ("seed", world.RunId), ("state", "complete"))).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    private async Task<IngestTestWorld> StartAsync() =>
        await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);

    private Task<TenantScope> AppScopeAsync(IngestTestWorld world) =>
        TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

    private Task<TenantScope> OwnerScopeAsync(IngestTestWorld world) =>
        TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));

    private static Task<string?> CompletedAtAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string?>($"select completed_at::text from public.run where id = '{runId:D}'");

    /// <summary>The shared window predicate, asked the way both admission readers compose it: one run row under <c>r</c>.</summary>
    private static string LateWindowSql(IngestTestWorld world) =>
        "select " + RunAdmissionPolicy.LateWindowOpenSql + $" from public.run r where r.id = '{world.RunId:D}'";

    /// <summary>Runs one statement as the owner that the schema must refuse, and returns the server error.</summary>
    private async Task<Npgsql.PostgresException> OwnerErrorAsync(IngestTestWorld world, string sql)
    {
        await using var scope = await OwnerScopeAsync(world);

        return await scope.CaptureErrorAsync(sql);
    }
}
