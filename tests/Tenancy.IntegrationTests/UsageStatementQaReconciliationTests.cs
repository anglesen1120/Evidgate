using System.Net;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class UsageStatementQaReconciliationTests(PostgresTenancyFixture fixture)
{
    private const string Period = "2026-10";
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Statement_SourceGapFlag_BlocksIssueOnlyWhenUsageIsKnownToBeMissing(bool gapFlag)
    {
        await using var world = await UsageReadsWorld.StartAsync(
            fixture, OperatorGateMode.Permitted, new FakeTimeProvider(Now));
        await world.SeedLedgerAsync(new LedgerSeed(
            Guid.CreateVersion7(), world.Project, "results", 1m, Period, "gap-source", Now,
            Amount: 5m, Currency: "USD"));
        await world.SeedRollupAsync(new RollupSeed(
            Guid.CreateVersion7(), world.Project, Period, "results", 1m, 1, Now,
            GapFlag: gapFlag, TotalAmount: 5m, Currency: "USD"));

        var reconciliation = await world.GetAsync(world.ReconciliationUrl($"period={Period}"));
        reconciliation.Status.ShouldBe(HttpStatusCode.OK);
        reconciliation.Body.GetProperty("gaps").GetArrayLength().ShouldBe(gapFlag ? 1 : 0);
        reconciliation.Body.GetProperty("units")[0].GetProperty("matches").GetBoolean().ShouldBeTrue();

        var issued = await world.PostStatementAsync(Period, 5m, "USD");

        issued.Status.ShouldBe(gapFlag ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK,
            "a source gap means the period is known to omit billable usage even when its stored sums agree");
        (await world.StatementCountAsync(Period)).ShouldBe(gapFlag ? 0L : 1L);
        (await world.AuditCountAsync("usage.statement_issued")).ShouldBe(gapFlag ? 0L : 1L);
        if (gapFlag)
        {
            issued.Body.GetProperty("error").GetProperty("details")[0]
                .GetProperty("reason").GetString().ShouldBe("not_reconciled");
        }
    }
}
