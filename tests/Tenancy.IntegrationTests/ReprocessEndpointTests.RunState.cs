// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessEndpointTests
{
    // ------------------------------------------------------------------ run state and accounting

    [Fact]
    public async Task A_canceled_run_is_accepted_and_its_state_is_unchanged()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);
        await SetRunStateAsync(world, runId, "canceled");

        var answer = await PostAsync(world, runId, reportId);

        answer.Status.ShouldBe(HttpStatusCode.Accepted);
        (await RunStateAsync(world, runId)).ShouldBe("canceled");
        (await ReprocessTasksAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_reprocess_writes_no_usage_ledger_and_no_event_batch_row()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        (await PostAsync(world, runId, reportId)).Status.ShouldBe(HttpStatusCode.Accepted);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var ledger = await scope.ScalarAsync<long>(
            "select count(*) from public.usage_ledger_entry where org_id = @org",
            ("org", world.Org));
        var batches = await scope.ScalarAsync<long>(
            "select count(*) from public.event_batch where org_id = @org",
            ("org", world.Org));

        ledger.ShouldBe(0);
        batches.ShouldBe(0);
    }
}
