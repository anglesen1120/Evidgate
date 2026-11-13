// The cancel that overlaps a finalize on the run's row lock: whichever order the two arrive in, both accept and the
// run stays canceled with its slots closed.
using System.Net;
using System.Net.Http.Json;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class CancelRunEndpointTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelRun_OverlappingFinalize_AcceptsBothAndStaysCanceled(bool cancelFirst)
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(),
            boundJti: IngestTestWorld.Jti, database: database, hosts: _hosts);
        await world.SetRunStateAsync("running");
        using var admin = world.Api.CreateClient();
        var hash = await world.ScalarAsync<string>("select m.manifest_hash from public.manifest m "
            + $"join public.run r on r.manifest_id = m.id and r.org_id = m.org_id where r.id = '{world.RunId:D}'");
        using var finalizeRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/runs/{world.RunId:D}/finalize");
        finalizeRequest.Headers.Add("Idempotency-Key", NewKey());
        finalizeRequest.Content = JsonContent.Create(new
        {
            producer_id = IngestTestWorld.ProducerId,
            shard = 1,
            last_sequence = 0,
            count = 0,
            manifest_hash = hash,
        });
        await using var blocker = await _fixture.OpenSuperuserAsync(database);
        await using var transaction = await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"select id from public.run where id = '{world.RunId:D}' for update";
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        Task<ApiAnswer> CancelAsync() => admin.SendAsync(HttpMethod.Post, $"/v1/runs/{world.RunId:D}/cancel",
            TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org), new { reason = "overlap finalize" }, NewKey());
        Task<HttpResponseMessage> FinalizeAsync() => world.Client.SendAsync(finalizeRequest, TestContext.Current.CancellationToken);
        Task<ApiAnswer> cancel;
        Task<HttpResponseMessage> finalize;
        if (cancelFirst)
        {
            cancel = CancelAsync();
            await WaitBlockedAsync(database, 1, cancel);
            finalize = FinalizeAsync();
        }
        else
        {
            finalize = FinalizeAsync();
            await WaitBlockedAsync(database, 1, finalize);
            cancel = CancelAsync();
        }

        await WaitBlockedAsync(database, 2, Task.WhenAll(cancel, finalize));
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        (await cancel).Status.ShouldBe(HttpStatusCode.Accepted);
        using var finalized = await finalize;
        finalized.StatusCode.ShouldBe(HttpStatusCode.Accepted,
            await finalized.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await world.ScalarAsync<string>($"select state from public.run where id = '{world.RunId:D}'")).ShouldBe("canceled");
        (await world.ScalarAsync<bool>($"select closed_at is not null from public.producer_slot where run_id = '{world.RunId:D}'"))
            .ShouldBeTrue();
        (await world.ScalarAsync<long>($"select count(*) from public.task where org_id = '{world.Org:D}' "
            + $"and task_type = '{CancelKillContract.TaskType}'")).ShouldBe(1);
    }

    private async Task WaitBlockedAsync(string database, int count, Task pending)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (await _fixture.ScalarAsync<int>(database,
            "select count(*)::int from pg_stat_activity where datname = current_database() "
            + "and wait_event_type = 'Lock' and pid <> pg_backend_pid()") < count)
        {
            pending.IsCompleted.ShouldBeFalse("both requests must overlap while the run lock is held");
            await Task.Delay(20, timeout.Token);
        }
    }
}
