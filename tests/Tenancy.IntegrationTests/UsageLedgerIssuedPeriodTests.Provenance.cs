using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// What the ledger refuses and preserves around a re-date: a writer never supplies <c>original_period</c> itself —
/// whether the month it names is open or issued — and moving a correction never touches the row it corrects.
/// </summary>
public sealed partial class UsageLedgerIssuedPeriodTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ledger_CallerSuppliedProvenance_RefusedWhateverTheMonth(bool issued)
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        if (issued)
        {
            await IssueAsAppAsync(world, world.Org, "2026-09");
        }

        // Positive control: the same row without a provenance is accepted, so the refusal below is the marker itself and
        // not the row's shape. Against an issued month the guard moves it; against an open one it stores it as it came.
        var controlKey = Unique.Slug("provenance-control");
        await using (var control = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(world.Org, world.Project, "2026-09", controlKey);
            (await control.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await control.CommitAsync();
        }

        await using (var supplied = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(
                world.Org, world.Project, "2026-09", Unique.Slug("provenance-supplied"), originalPeriod: "2026-08");
            var refusal = await supplied.CaptureErrorAsync(row.Sql, row.Parameters);

            refusal.SqlState.ShouldBe("23514");
            refusal.ConstraintName.ShouldBe(SuppliedPeriod);
            refusal.MessageText.Contains("2026-08", StringComparison.Ordinal)
                .ShouldBeFalse("the refusal never repeats the value the caller supplied");
        }

        (await RowPeriodAsync(world, controlKey))
            .ShouldBe(issued ? "2026-10|2026-09" : "2026-09|-", "the control row is stored as its own month allows");
        (await world.ScalarAsync<long>(
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}'"))
            .ShouldBe(1, "only the otherwise identical row without caller provenance persists");
    }

    [Fact]
    public async Task Ledger_RedatedAdjustment_LeavesTheOriginalRowByteIdentical()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var original = Guid.CreateVersion7();
        await using (var open = await world.BeginAppScopeAsync())
        {
            var row = LedgerRow(world.Org, world.Project, "2026-10", Unique.Slug("adjusted"), original);
            (await open.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await open.CommitAsync();
        }

        var before = await world.ScalarAsync<string>(
            $"select to_jsonb(l)::text from public.usage_ledger_entry l where id = '{original:D}'");
        await IssueAsAppAsync(world, world.Org, "2026-09");

        const string Correction = "adjustment-late";
        await using (var issued = await world.BeginAppScopeAsync())
        {
            var row = AdjustmentRow(world.Org, world.Project, "2026-09", Correction, original);
            (await issued.ExecuteAsync(row.Sql, row.Parameters)).ShouldBe(1);
            await issued.CommitAsync();
        }

        (await RowPeriodAsync(world, Correction)).ShouldBe("2026-10|2026-09");
        (await world.ScalarAsync<string>(
            $"select to_jsonb(l)::text from public.usage_ledger_entry l where id = '{original:D}'"))
            .ShouldBe(before, "a re-date writes the correction's own row and never rewrites the row it corrects");
        (await world.ScalarAsync<string>(
            $"select adjustment_of::text || '|' || quantity::text || '|' || reason "
            + $"from public.usage_ledger_entry where org_id = '{world.Org:D}' and billing_key = '{Correction}'"))
            .ShouldBe($"{original:D}|-1|correction");
    }
}
