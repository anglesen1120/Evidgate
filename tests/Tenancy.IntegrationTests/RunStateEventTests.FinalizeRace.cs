using System.Diagnostics;
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class RunStateEventTests
{
    [Fact]
    public async Task CancelRacingFinalize_RecordsOnlyCommittedEdgesAndEndsCanceled()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, database: database.Name);
        await world.SetRunStateAsync("running");
        using var admin = world.Api.CreateClient();
        var manifestHash = await world.ScalarAsync<string>(
            $"select manifest_hash from public.manifest where id = (select manifest_id from public.run where id = '{world.RunId:D}')");
        ApiAnswer? cancelAnswer = null;
        ApiAnswer? finalizeAnswer = null;
        Task? cancel = null;
        Task? finalize = null;
        await using (var blocker = await RunTransitionScope.BeginAsync(world.Api, world.Org))
        {
            (await blocker.ScalarAsync<string>($"select state from public.run where id = '{world.RunId:D}' for no key update"))
                .ShouldBe("running");
            try
            {
                cancel = Detached(async () => cancelAnswer = await admin.SendAsync(
                    HttpMethod.Post, $"/v1/runs/{world.RunId:D}/cancel",
                    TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org),
                    new { reason = "overlapping finalize" }, RegistryApi.NewKey()), TestContext.Current.CancellationToken);
                finalize = Detached(async () => finalizeAnswer = await world.Client.SendAsync(
                    HttpMethod.Post, $"/v1/runs/{world.RunId:D}/finalize", static _ => { },
                    new { producer_id = IngestTestWorld.ProducerId, shard = 1, last_sequence = 0, count = 0, manifest_hash = manifestHash },
                    RegistryApi.NewKey()), TestContext.Current.CancellationToken);
                await AwaitBothHistoryWritersBlockedAsync(world, cancel, finalize);
            }
            finally
            {
                await blocker.CommitAsync();
            }
        }

        await Task.WhenAll(cancel.ShouldNotBeNull(), finalize.ShouldNotBeNull())
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        cancelAnswer.ShouldNotBeNull().Status.ShouldBe(HttpStatusCode.Accepted, cancelAnswer?.ErrorMessage);
        finalizeAnswer.ShouldNotBeNull().Status.ShouldBe(HttpStatusCode.Accepted, finalizeAnswer?.ErrorMessage);
        (await world.ScalarAsync<string>($"select state from public.run where id = '{world.RunId:D}'")).ShouldBe("canceled");
        var edges = await _fixture.ColumnAsync<string>(world.Database,
            "select coalesce(from_state, '(insert)') || '->' || to_state from public.run_state_event "
            + $"where run_id = '{world.RunId:D}' order by seq");
        edges[^1].ShouldBeOneOf("running->canceled", "collecting->canceled");
        edges.ShouldNotContain(edge => edge.StartsWith("canceled->", StringComparison.Ordinal));
        edges.ShouldNotContain(edge => edge.EndsWith("->complete", StringComparison.Ordinal));
        (await _fixture.ColumnAsync<int>(world.Database,
            $"select seq from public.run_state_event where run_id = '{world.RunId:D}' order by seq"))
            .ShouldBe(Enumerable.Range(1, edges.Count));
    }

    private static async Task AwaitBothHistoryWritersBlockedAsync(IngestTestWorld world, Task cancel, Task finalize)
    {
        var elapsed = Stopwatch.StartNew();
        while (await world.ScalarAsync<int>(
            "select count(*)::int from pg_stat_activity where datname = current_database() "
            + "and wait_event_type = 'Lock' and pid <> pg_backend_pid()") < 2)
        {
            cancel.IsCompleted.ShouldBeFalse("the cancel must overlap the finalize while the run row is locked");
            finalize.IsCompleted.ShouldBeFalse("the finalize must overlap the cancel while the run row is locked");
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
