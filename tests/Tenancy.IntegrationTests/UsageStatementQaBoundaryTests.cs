using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class UsageStatementQaBoundaryTests(PostgresTenancyFixture fixture)
{
    private const string Period = "2026-10";
    private const string Earlier = "2026-09";
    private const string IssueAction = "usage.statement_issued";
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("10.49")]
    [InlineData("10.51")]
    public async Task Statement_MinorUnitMismatch_RefusesWithoutSideEffectsAndAcceptsExactTotal(string submitted)
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "minor-unit", 10.50m));

        var refused = await world.PostStatementAsync(Period,
            decimal.Parse(submitted, System.Globalization.CultureInfo.InvariantCulture), "USD");

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        Reason(refused).ShouldBe("total_mismatch");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.AuditCountAsync(IssueAction)).ShouldBe(0L);
        (await world.PostStatementAsync(Period, 10.50m, "USD")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Statement_NegativeAdjustment_IssuesNetAmountAndFreezesBothRows()
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "debit", 10.50m);
        await world.SeedLedgerAsync(original, Adjustment(world, original, "credit", -3.25m));
        var before = await world.GetTextAsync(world.WorksheetUrl($"period={Period}"));

        var issued = await world.PostStatementAsync(Period, 7.25m, "USD");

        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        issued.Body.GetProperty("total_amount").GetDecimal().ShouldBe(7.25m);
        var stored = (await world.StatementRowAsync(Period)).ShouldNotBeNull().Split('|', 8);
        using var document = JsonDocument.Parse(stored[7]);
        document.RootElement.GetProperty("lines")[0].GetProperty("amount").GetString().ShouldBe("10.5");
        document.RootElement.GetProperty("adjustments")[0].GetProperty("amount").GetString().ShouldBe("-3.25");
        document.RootElement.GetProperty("total").GetString().ShouldBe("7.25");
        (await world.GetTextAsync(world.WorksheetUrl($"period={Period}"))).Text.ShouldBe(before.Text);
    }

    [Fact]
    public async Task Statement_EmptyPeriod_RefusesAndAcceptsThePeriodOnceItHasRows()
    {
        await using var world = await StartAsync();
        var refused = await world.PostStatementAsync(Period, 0m, "USD");

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        Reason(refused).ShouldBe("no_rows");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.AuditCountAsync(IssueAction)).ShouldBe(0L);

        await world.SeedLedgerAsync(Ledger(world, "zero-priced", 0m));
        (await world.PostStatementAsync(Period, 0m, "USD")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("-2.75")]
    [InlineData("0")]
    public async Task Statement_AdjustmentsOnlyPeriod_IssuesItsCreditOrZeroWithoutBillingTheOriginal(string amount)
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "earlier", 10m) with { Period = Earlier };
        var credit = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        await world.SeedLedgerAsync(original, Adjustment(world, original, "current-credit", credit));

        var issued = await world.PostStatementAsync(Period, credit, "USD");

        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        issued.Body.GetProperty("total_amount").GetDecimal().ShouldBe(credit);
        issued.Body.GetProperty("line_count").GetInt32().ShouldBe(0);
        var worksheet = await world.GetAsync(world.WorksheetUrl($"period={Period}"));
        worksheet.Body.GetProperty("lines").GetArrayLength().ShouldBe(0);
        worksheet.Body.GetProperty("adjustments").GetArrayLength().ShouldBe(1);
        worksheet.Body.GetProperty("line_total").GetDecimal().ShouldBe(0m);
        worksheet.Body.GetProperty("adjustment_total").GetDecimal().ShouldBe(credit);
        (await world.StatementCountAsync(Earlier)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_ConcurrentIssueCalls_CommitOneDocumentAndExactlyOneAudit()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "concurrent", 7.25m));
        var answers = await Task.WhenAll(
            world.PostStatementAsync(Period, 7.25m, "USD"),
            world.PostStatementAsync(Period, 7.25m, "USD"));

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
        answers.Count(answer => answer.Body.GetProperty("replayed").GetBoolean()).ShouldBe(1);
        answers.Select(answer => answer.Body.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(1);
        (await world.StatementCountAsync(Period)).ShouldBe(1L);
        (await world.AuditCountAsync(IssueAction)).ShouldBe(1L);
    }

    [Fact]
    public async Task Statement_AnotherOrgsPeriod_DoesNotLeakIntoOwnWorksheetOrStatement()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await world.SeedLedgerAsync(Ledger(world, "foreign-period", 99m) with
        {
            Org = otherOrg,
            ProjectId = otherProject,
        });

        var refused = await world.PostStatementAsync(Period, 99m, "USD");
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        Reason(refused).ShouldBe("no_rows");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.AuditCountAsync(IssueAction)).ShouldBe(0L);
        var own = await world.GetAsync(world.WorksheetUrl($"period={Period}"));
        own.Body.GetProperty("lines").GetArrayLength().ShouldBe(0);

        var owned = await world.PostStatementAsync(Period, 99m, "USD", otherOrg);
        owned.Status.ShouldBe(HttpStatusCode.OK, owned.ErrorMessage);
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.AuditCountForAsync(otherOrg, IssueAction)).ShouldBe(1L);
    }

    [Fact]
    public async Task Statement_ExactlyAtWorksheetRowLimit_IssuesTheCompleteDocument()
    {
        await using var world = await StartAsync();
        await world.SeedManyPricedLedgerRowsAsync(10_000, Period, "results", 1m, 0.01m, "USD");

        var worksheet = await world.GetAsync(world.WorksheetUrl($"period={Period}"));
        worksheet.Status.ShouldBe(HttpStatusCode.OK);
        worksheet.Body.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        worksheet.Body.GetProperty("reconciled").GetBoolean().ShouldBeTrue();
        worksheet.Body.GetProperty("total").GetDecimal().ShouldBe(100m);
        var issued = await world.PostStatementAsync(Period, 100m, "USD");
        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        issued.Body.GetProperty("line_count").GetInt32().ShouldBe(10_000);
        (await world.StatementCountAsync(Period)).ShouldBe(1L);
    }

    [Fact]
    public async Task Statement_CancellingAdjustment_IssuesZeroRatherThanTreatingPeriodAsEmpty()
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "net-zero-original", 2.75m);
        await world.SeedLedgerAsync(original, Adjustment(world, original, "net-zero-credit", -2.75m));

        var issued = await world.PostStatementAsync(Period, 0m, "USD");

        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        issued.Body.GetProperty("total_amount").GetDecimal().ShouldBe(0m);
        issued.Body.GetProperty("line_count").GetInt32().ShouldBe(1);
    }

    private Task<UsageReadsWorld> StartAsync() =>
        UsageReadsWorld.StartAsync(fixture, OperatorGateMode.Permitted, new FakeTimeProvider(Now));

    private static LedgerSeed Ledger(UsageReadsWorld world, string key, decimal amount) =>
        new(Guid.CreateVersion7(), world.Project, "results", 1m, Period, key, Now,
            Amount: amount, Currency: "USD");

    private static LedgerSeed Adjustment(UsageReadsWorld world, LedgerSeed original, string key, decimal amount) =>
        Ledger(world, key, amount) with
        {
            Quantity = -1m,
            AdjustmentOf = original.Id,
            Reason = "duplicate usage withdrawn",
        };

    private static string? Reason(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString();
}
