// What a late write still refuses and what one transaction means: a foreign project is refused before the guard can
// move anything, the walk crosses a year boundary through both callers, and a row that cannot be billed takes its
// whole transaction — or its whole batch — with it.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class UsageRecordBatchTests
{
    [Theory]
    [InlineData(true, UsageMetric.Results)]
    [InlineData(false, UsageMetric.Results)]
    [InlineData(true, UsageMetric.ArtifactGbMonth)]
    [InlineData(false, UsageMetric.ArtifactGbMonth)]
    public async Task LateBatch_ForeignProject_RefusedBeforeAnyRedate(bool commandPath, UsageMetric metric)
    {
        await using var world = await StartAsync();
        var founder = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, founder, Unique.Slug("foreign"));
        var foreignProject = await CreateProjectAsync(world.Client, founder, otherOrg, Unique.Slug("foreign"));
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        var entry = Entry("late:foreign", foreignProject, period: IssuedPeriod, metric: metric);
        if (commandPath)
        {
            var refused = await SendAsync(world, Batch("foreign-late", [entry]));

            refused.IsSuccess.ShouldBeFalse();
            refused.Error!.Status.ShouldBe(422);
            refused.Error.Details!.ShouldHaveSingleItem().Path.ShouldBe("/entries/0/project_id");
        }
        else
        {
            var refused = await Should.ThrowAsync<ArgumentException>(() => RecordAsync(
                world, [entry], cancellationToken: TestContext.Current.CancellationToken));

            refused.Message.ShouldContain("/entries/0/project_id");
        }

        (await LedgerCountAsync(world, world.Org)).ShouldBe(0, "a refused entry writes nothing to either tenant");
        (await LedgerCountAsync(world, otherOrg)).ShouldBe(0);
        (await RedateRowsAsync(world)).ShouldBeEmpty("the project is checked before the guard can decide a move");

        // Control: the same row with the caller's own project is accepted and re-dated in the same month, so the refusal
        // above was the project and not the late period.
        var control = entry with { ProjectId = world.Project };
        if (commandPath)
        {
            var accepted = await SendAsync(world, Batch("foreign-late-control", [control]));

            accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
            accepted.Value!.Inserted.ShouldBe([control.BillingKey]);
        }
        else
        {
            (await RecordAsync(world, [control], cancellationToken: TestContext.Current.CancellationToken))
                .Inserted.ShouldBe([control.BillingKey]);
        }

        (await ColumnAsync(world, world.Org, control.BillingKey, "period")).ShouldBe(Period);
        (await ColumnAsync(world, world.Org, control.BillingKey, "original_period")).ShouldBe(IssuedPeriod);
        (await RedateRowsAsync(world)).ShouldBe([$"{IssuedPeriod}|{Period}|1"]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task LateBatch_YearBoundary_LandsInTheFirstOpenMonthOfTheNextYear(
        bool commandPath,
        bool januaryIssued)
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, "2026-12");
        if (januaryIssued)
        {
            await IssueStatementAsync(world, world.Org, "2027-01");
        }

        var entry = Entry("late:year", world.Project, period: "2026-12");
        if (commandPath)
        {
            var answer = await SendAsync(world, Batch("late-year", [entry]));

            answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
            answer.Value!.Inserted.ShouldBe([entry.BillingKey]);
        }
        else
        {
            (await RecordAsync(world, [entry], cancellationToken: TestContext.Current.CancellationToken))
                .Inserted.ShouldBe([entry.BillingKey]);
        }

        var assigned = januaryIssued ? "2027-02" : "2027-01";
        (await ColumnAsync(world, world.Org, entry.BillingKey, "period"))
            .ShouldBe(assigned, "December rolls into January, and a January that is issued is skipped by name");
        (await ColumnAsync(world, world.Org, entry.BillingKey, "original_period")).ShouldBe("2026-12");
        (await RedateRowsAsync(world)).ShouldBe([$"2026-12|{assigned}|1"]);
    }

    [Fact]
    public async Task RecordUsageBatch_NoOpenMonthAfterOneRow_RollsBackTheWholeBatch()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, "9999-12");

        var answer = await SendAsync(
            world,
            Batch(
                "usage-mixed-end",
                [
                    Entry("a:open-first", world.Project),
                    Entry("z:no-month", world.Project, period: "9999-12"),
                ]));

        answer.IsSuccess.ShouldBeFalse();
        answer.Error!.Status.ShouldBe(409);
        answer.Error.Code.ShouldBe("no_open_period");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0, "one refused row refuses the batch it travelled in");
        (await AuditCountAsync(world, "usage.record_batch")).ShouldBe(0, "a failed command writes no audit row");
        (await RedateRowsAsync(world)).ShouldBeEmpty();

        var control = await SendAsync(world, Batch("usage-mixed-end-control", [Entry("a:open-first", world.Project)]));

        control.IsSuccess.ShouldBeTrue(control.Error?.Message);
        control.Value!.Inserted.ShouldBe(["a:open-first"], "the entry that could be billed is billed on its own");
    }

    [Fact]
    public async Task RecordAsync_LateRowWithoutCommit_RollsBackRowAndRedateAuditTogether()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var token = TestContext.Current.CancellationToken;
        await using (var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, token))
        {
            var answer = await provider.GetRequiredService<IUsageLedger>().RecordAsync(
                new RecordUsageBatchRequest([Entry("rollback:late", world.Project, period: IssuedPeriod)]), token);

            answer.Inserted.ShouldBe(["rollback:late"], "the guard moves the row inside the caller's transaction");
        }

        (await LedgerCountAsync(world, world.Org)).ShouldBe(0, "nothing commits without the caller's commit");
        (await RedateRowsAsync(world)).ShouldBeEmpty("the move's audit row belongs to the transaction that wrote it");

        (await RecordAsync(
                world, [Entry("rollback:late", world.Project, period: IssuedPeriod)],
                cancellationToken: TestContext.Current.CancellationToken))
            .Inserted.ShouldBe(["rollback:late"]);
        (await RedateRowsAsync(world)).ShouldBe([$"{IssuedPeriod}|{Period}|1"]);
    }
}
