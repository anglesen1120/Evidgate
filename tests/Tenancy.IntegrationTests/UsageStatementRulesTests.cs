using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Usage.Application.Queries;
using Evidgate.Modules.Usage.Application.Billing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The rules of the invoice worksheet and its statement, without a database: which refusal a period earns and in
/// which order, how a submitted total is compared, and the canonical text and hash an issued document is checked
/// against.
/// </summary>
/// <remarks>
/// These are the invariants a review or a later refactor can break without any route changing, so they are pinned
/// here on hand-built worksheets: an unpriced period is refused for its pricing rather than for a sum it never had,
/// a reconciliation that disagrees with the ledger — or a roll-up gap, or no compared side at all — fires
/// <c>not_reconciled</c> rather than a currency it cannot name, a document past what a statement row stores never
/// reaches the insert, and the hash ignores the scale a decimal happens to be stored at.
/// </remarks>
public sealed class UsageStatementRulesTests
{
    private const string Period = "2026-10";

    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid LineId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectId = new("22222222-2222-2222-2222-222222222222");
    private static readonly string LongestReason = new('x', 2_000);

    [Theory]
    [InlineData(null, "required")]
    [InlineData("2026-1", "format")]
    [InlineData("2026-13", "format")]
    [InlineData("2026-10-01", "format")]
    [InlineData("abcd-ef", "format")]
    public void ValidatePeriod_NamesWhatIsWrong(string? period, string reason)
    {
        var validated = StatementIssueRules.ValidatePeriod(period);

        validated.IsSuccess.ShouldBeFalse();
        validated.Error!.Status.ShouldBe(422);
        validated.Error.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        validated.Error.Details!.ShouldHaveSingleItem().Path.ShouldBe("period");
        validated.Error.Details![0].Reason.ShouldBe(reason);
    }

    [Theory]
    [InlineData("2026-09", true)]
    [InlineData("2026-10", true)]
    [InlineData("2026-11", false)]
    [InlineData("2026-12", false)]
    [InlineData("2027-01", false)]
    public void IsClosed_EndsTheMonthBeforeTheCurrentOne(string period, bool closed)
    {
        StatementIssueRules.IsClosed(period, Now).ShouldBe(closed,
            "a period is billable once its month is over, whatever day of the current month it is");

        StatementIssueRules.ValidatePeriod(period).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_EmptyPeriod_NamesNoRows()
    {
        var result = Evaluate(Sheet([]), total: 0m, currency: "USD");

        Reason(result).ShouldBe(StatementIssueRules.ReasonNoRows);
        result.Error!.Details![0].Path.ShouldBe("period");
    }

    [Fact]
    public void Evaluate_UnpricedRows_AreJudgedBeforeTheTotal()
    {
        // A period with an unpriced row has no total at all, so its reconciliation is "unknown", not "failed":
        // the refusal the caller needs is the pricing, not a sum that never existed.
        var sheet = Sheet([Line(1m, 2m), Line(2m, null)], lineTotal: null, total: null, unpriced: 1, reconciled: false);

        Reason(Evaluate(sheet, total: 2m, currency: "USD")).ShouldBe(StatementIssueRules.ReasonUnpricedRows);
    }

    [Fact]
    public void Evaluate_MixedCurrencyPeriod_NamesTheCurrencyRatherThanTheTotal()
    {
        // A reconciled period whose rows carry two currencies states no single currency and no total; the caller
        // is told the currency it cannot sum, not the total it never had.
        var sheet = Sheet([Line(1m, 2m), Line(2m, 3m, "EUR")], lineTotal: 5m, total: null, currency: null);

        Reason(Evaluate(sheet, total: 5m, currency: "USD")).ShouldBe(StatementIssueRules.ReasonMixedCurrency);
    }

    [Fact]
    public void Evaluate_RowsWithoutAComparedLedgerSide_IsNotReconciledRatherThanMixed()
    {
        // What an empty units read produces beside one priced row: no currency and a total of zero, because the
        // ledger side states neither. The rows are the period's own, so the refusal is the reconciliation.
        var sheet = Sheet([Line(1m, 2m)], lineTotal: 2m, total: 0m, currency: null, reconciled: false);

        var result = Evaluate(sheet, total: 0m, currency: "USD");

        Reason(result).ShouldBe(StatementIssueRules.ReasonNotReconciled);
        result.Error!.Details![0].Path.ShouldBe("period");
    }

    [Fact]
    public void Evaluate_SourceGap_IsRefusedEvenWhenEverySumAgrees()
    {
        // The roll-up flagged usage the ledger holds no row for: the rows agree with the aggregate and still
        // omit billable usage, so the period is not reconciled.
        var sheet = Sheet([Line(1m, 5m)], lineTotal: 5m, total: 5m, sourceGaps: 1);

        var result = Evaluate(sheet, total: 5m, currency: "USD");

        Reason(result).ShouldBe(StatementIssueRules.ReasonNotReconciled);
        result.Error!.Details![0].Path.ShouldBe("period");
    }

    [Fact]
    public void Evaluate_CurrencyWithoutATotal_IsRefusedRatherThanBilled()
    {
        var sheet = Sheet([Line(1m, 5m)], lineTotal: 5m, total: null);

        Reason(Evaluate(sheet, total: 5m, currency: "USD")).ShouldBe(StatementIssueRules.ReasonNotReconciled);
    }

    [Fact]
    public void Evaluate_UnreconciledPeriod_IsRefusedEvenWhenTheTotalMatches()
    {
        var sheet = Sheet([Line(1m, 5m)], lineTotal: 5m, total: 5m, reconciled: false);

        Reason(Evaluate(sheet, total: 5m, currency: "USD")).ShouldBe(StatementIssueRules.ReasonNotReconciled);
    }

    [Fact]
    public void Evaluate_TruncatedWorksheet_IsNeverIssuable()
    {
        var sheet = Sheet([Line(1m, 5m)], lineTotal: 5m, total: 5m, truncated: true);

        Reason(Evaluate(sheet, total: 5m, currency: "USD")).ShouldBe(StatementIssueRules.ReasonNotReconciled);
    }

    [Fact]
    public void Evaluate_TotalAtAnotherScale_IsTheSameTotal()
    {
        var sheet = Sheet([Line(1m, 5m)], lineTotal: 5m, total: 5.00m);

        var result = Evaluate(sheet, total: 5.0000m, currency: "USD");

        result.IsSuccess.ShouldBeTrue("decimal equality compares values, so 5.0000 and 5.00 are one total");
        result.Value.TotalAmount.ShouldBe(5m);
        result.Value.ContentHash.ShouldBe(sheet.ContentHash);
        result.Value.WorksheetJson.ShouldBe(InvoiceWorksheetCanonical.ToJson(sheet));
    }

    [Fact]
    public void Evaluate_TotalOrCurrencyThatDiffers_IsATotalMismatch()
    {
        var sheet = Sheet([Line(1m, 5m)], lineTotal: 5m, total: 5m);

        var total = Evaluate(sheet, total: 5.01m, currency: "USD");
        Reason(total).ShouldBe(StatementIssueRules.ReasonTotalMismatch);
        total.Error!.Details![0].Path.ShouldBe("total_amount");

        var currency = Evaluate(sheet, total: 5m, currency: "EUR");
        Reason(currency).ShouldBe(StatementIssueRules.ReasonTotalMismatch);
        currency.Error!.Details![0].Path.ShouldBe("currency");
    }

    [Fact]
    public void Evaluate_CorrectionsOnlyPeriod_IssuesItsNegativeTotal()
    {
        var correction = Line(-2m, -2m) with { AdjustmentOf = LineId, Reason = "billed twice" };
        var sheet = Sheet([], [correction], lineTotal: 0m, adjustmentTotal: -2m, total: -2m);

        var result = Evaluate(sheet, total: -2m, currency: "USD");

        result.IsSuccess.ShouldBeTrue("a period of corrections bills a credit, not an error");
        result.Value.TotalAmount.ShouldBe(-2m);
        result.Value.LineCount.ShouldBe(0);
    }

    [Fact]
    public void Canonical_IdenticalWorksheets_RenderTheSameTextAndHash()
    {
        var first = Sheet([Line(1m, 2m), Line(2m, 3.50m)], lineTotal: 5.50m, total: 5.50m);
        var second = Sheet([Line(1m, 2m), Line(2m, 3.50m)], lineTotal: 5.50m, total: 5.50m);

        InvoiceWorksheetCanonical.ToJson(second).ShouldBe(InvoiceWorksheetCanonical.ToJson(first));
        InvoiceWorksheetCanonical.Hash(second).ShouldBe(InvoiceWorksheetCanonical.Hash(first));
        InvoiceWorksheetCanonical.Hash(first).ShouldMatch("^[0-9a-f]{64}$");
        InvoiceWorksheetCanonical.ToJson(first).Contains("generated", StringComparison.Ordinal).ShouldBeFalse(
            "a document that names its generation instant could never be compared with itself");
    }

    [Fact]
    public void Canonical_AnyChangeToARow_ChangesTheHash()
    {
        var priced = Sheet([Line(1m, 2m)], lineTotal: 2m, total: 2m);
        var repriced = Sheet([Line(1m, 3m)], lineTotal: 3m, total: 3m);

        InvoiceWorksheetCanonical.Hash(repriced).ShouldNotBe(InvoiceWorksheetCanonical.Hash(priced));
    }

    [Fact]
    public void Canonical_Decimals_AreWrittenWithoutTheirScale()
    {
        InvoiceWorksheetCanonical.Text(10.50m).ShouldBe("10.5");
        InvoiceWorksheetCanonical.Text(10.5m).ShouldBe("10.5");
        InvoiceWorksheetCanonical.Text(10.5000m).ShouldBe("10.5");
        InvoiceWorksheetCanonical.Text(0.00m).ShouldBe("0");
        InvoiceWorksheetCanonical.Text(-0.000m).ShouldBe("0");
        InvoiceWorksheetCanonical.Text(-2.25m).ShouldBe("-2.25");
    }

    [Fact]
    public void Canonical_FreeTextInACorrection_IsEscapedAndStable()
    {
        var correction = Line(-1m, -2m) with
        {
            AdjustmentOf = LineId,
            Reason = "supplier said \"void\"\nand re-sent",
        };
        var sheet = Sheet([], [correction], lineTotal: 0m, adjustmentTotal: -2m, total: -2m);

        using var document = JsonDocument.Parse(InvoiceWorksheetCanonical.ToJson(sheet));

        document.RootElement.GetProperty("adjustments")[0].GetProperty("reason").GetString()
            .ShouldBe("supplier said \"void\"\nand re-sent");
        InvoiceWorksheetCanonical.Hash(sheet).ShouldBe(
            InvoiceWorksheetCanonical.Hash(Sheet([], [correction], 0m, -2m, -2m)));
    }

    [Fact]
    public void Canonical_RowNeverReDated_KeepsTheDocumentItHadBeforeTheMarker()
    {
        var sheet = Sheet([Line(1m, 2m)], lineTotal: 2m, total: 2m);

        InvoiceWorksheetCanonical.ToJson(sheet).Contains("original_period", StringComparison.Ordinal)
            .ShouldBeFalse(
                "the marker is written only where a re-date happened, so an untouched period keeps its bytes");
    }

    [Fact]
    public void Canonical_ReDatedRow_WritesTheMonthItWasMeteredForAndMovesTheHash()
    {
        var redated = Line(1m, 2m) with { OriginalPeriod = "2026-09" };
        var sheet = Sheet([redated], lineTotal: 2m, total: 2m);

        using var document = JsonDocument.Parse(InvoiceWorksheetCanonical.ToJson(sheet));
        document.RootElement.GetProperty("lines")[0].GetProperty("original_period").GetString()
            .ShouldBe("2026-09");

        InvoiceWorksheetCanonical.Hash(sheet).ShouldNotBe(
            InvoiceWorksheetCanonical.Hash(Sheet([Line(1m, 2m)], lineTotal: 2m, total: 2m)),
            "a statement issued before the row was re-dated must not match the document that now carries it");
    }

    [Fact]
    public void WorksheetTotals_ReconcilesCountAmountAndCurrency()
    {
        var ledger = LedgerSide(total: 5m, currency: "USD", rowCount: 2);
        var rows = WorksheetTotals.FromRows([Line(1m, 2m), Line(2m, 3m)], []);

        WorksheetTotals.IsReconciled(ledger, rows, unitsTruncated: false).ShouldBeTrue();

        WorksheetTotals.IsReconciled(LedgerSide(6m, "USD", 2), rows, false)
            .ShouldBeFalse("a ledger total the rows do not add up to is a reconciliation failure");
        WorksheetTotals.IsReconciled(LedgerSide(5m, "USD", 3), rows, false)
            .ShouldBeFalse("the ledger covers three rows and the worksheet two");
        WorksheetTotals.IsReconciled(LedgerSide(5m, "EUR", 2), rows, false)
            .ShouldBeFalse("the two sides bill different currencies");
        WorksheetTotals.IsReconciled(LedgerSide(5m, "USD", 2), rows, unitsTruncated: true)
            .ShouldBeFalse("a cut aggregate cannot be compared with the whole period");
    }

    [Fact]
    public void WorksheetTotals_AnUnknownAmountCannotDisagree()
    {
        var ledger = LedgerSide(total: null, currency: null, rowCount: 2);
        var rows = WorksheetTotals.FromRows([Line(1m, 2m), Line(2m, null)], []);

        rows.UnpricedCount.ShouldBe(1);
        rows.LineTotal.ShouldBeNull();
        WorksheetTotals.IsReconciled(ledger, rows, false)
            .ShouldBeTrue("both sides are unknown, so the counts are all there is to compare");
    }

    [Fact]
    public void WorksheetTotals_EmptyPeriod_ReconcilesAtZero()
    {
        var ledger = WorksheetTotals.FromUnits(new ReconciliationUnits([], [], Truncated: false));
        var rows = WorksheetTotals.FromRows([], []);

        ledger.Total.ShouldBe(0m);
        ledger.Currency.ShouldBeNull();
        rows.LineTotal.ShouldBe(0m);
        rows.AdjustmentTotal.ShouldBe(0m);
        WorksheetTotals.IsReconciled(ledger, rows, false).ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_WorksheetAtTheDocumentCap_IssuesAndOneMoreCorrectionIsRefused()
    {
        var (fits, over) = CorrectionsAroundTheCap();

        var issued = Evaluate(CorrectionSheet(fits), total: -fits, currency: "USD");

        issued.IsSuccess.ShouldBeTrue("the largest document a statement row can store is issuable");
        Encoding.UTF8.GetByteCount(issued.Value.WorksheetJson)
            .ShouldBeLessThanOrEqualTo(StatementIssueRules.MaxWorksheetBytes);

        var refused = Evaluate(CorrectionSheet(over), total: -over, currency: "USD");

        Reason(refused).ShouldBe(StatementIssueRules.ReasonWorksheetTooLarge);
        refused.Error!.Details![0].Path.ShouldBe("period");
    }

    [Fact]
    public void WorksheetTotals_CoversSameRows_ComparesCountTotalAndCurrency()
    {
        var sheet = Sheet([Line(1m, 2m), Line(2m, 3m)], lineTotal: 5m, total: 5m);

        WorksheetTotals.CoversSameRows(LedgerSide(5m, "USD", 2), sheet).ShouldBeTrue();

        WorksheetTotals.CoversSameRows(LedgerSide(5m, "USD", 3), sheet)
            .ShouldBeFalse("a row arrived after the worksheet was assembled");
        WorksheetTotals.CoversSameRows(LedgerSide(6m, "USD", 2), sheet)
            .ShouldBeFalse("the period's amount moved");
        WorksheetTotals.CoversSameRows(LedgerSide(5m, "EUR", 2), sheet)
            .ShouldBeFalse("the period's currency moved");
        WorksheetTotals.CoversSameRows(LedgerSide(5m, "USD", 2, truncated: true), sheet)
            .ShouldBeFalse("a cut aggregate can drop the scope a late row created, so it proves nothing");
    }

    private static WorksheetLine Line(decimal quantity, decimal? amount, string currency = "USD") => new(
        LineId,
        ProjectId,
        "results",
        quantity,
        "batch",
        "key:1",
        OccurredAt,
        PriceRef: null,
        amount,
        amount is null ? null : currency,
        AdjustmentOf: null,
        Reason: null,
        OriginalPeriod: null);

    /// <summary>
    /// A correction carrying the longest reason the ledger accepts. Its length is fixed by <paramref name="index"/>
    /// so a row's canonical text is the same size whatever the index is.
    /// </summary>
    private static WorksheetLine Correction(int index) => Line(-1m, -1m) with
    {
        BillingKey = $"key:{index:D5}",
        AdjustmentOf = LineId,
        Reason = LongestReason,
    };

    /// <summary>A corrections-only worksheet of <paramref name="count"/> longest-reason rows, all at one amount.</summary>
    private static InvoiceWorksheet CorrectionSheet(int count) =>
        Sheet(
            [],
            Enumerable.Range(0, count).Select(Correction).ToArray(),
            lineTotal: 0m,
            adjustmentTotal: -count,
            total: -count);

    /// <summary>
    /// The most longest-reason corrections whose canonical document fits the cap, and the fewest that do not. The
    /// estimate is per-row arithmetic; the two loops measure the documents around it, because the totals' decimal
    /// text grows with the count.
    /// </summary>
    private static (int Fits, int Over) CorrectionsAroundTheCap()
    {
        var one = Bytes(CorrectionSheet(1));
        var perRow = Bytes(CorrectionSheet(2)) - one;
        var fits = (StatementIssueRules.MaxWorksheetBytes - one) / perRow;

        while (Bytes(CorrectionSheet(fits)) > StatementIssueRules.MaxWorksheetBytes)
        {
            fits--;
        }

        var over = fits + 1;
        while (Bytes(CorrectionSheet(over)) <= StatementIssueRules.MaxWorksheetBytes)
        {
            over++;
        }

        return (fits, over);
    }

    private static int Bytes(InvoiceWorksheet worksheet) =>
        Encoding.UTF8.GetByteCount(InvoiceWorksheetCanonical.ToJson(worksheet));

    private static InvoiceWorksheet Sheet(
        IReadOnlyList<WorksheetLine> lines,
        IReadOnlyList<WorksheetLine>? adjustments = null,
        decimal? lineTotal = null,
        decimal? adjustmentTotal = null,
        decimal? total = null,
        string? currency = "USD",
        int unpriced = 0,
        int sourceGaps = 0,
        bool truncated = false,
        bool reconciled = true)
    {
        var corrections = adjustments ?? [];
        var sheet = new InvoiceWorksheet(
            Period,
            currency,
            lines,
            corrections,
            lineTotal,
            adjustmentTotal,
            total,
            lines.Count,
            unpriced,
            sourceGaps,
            truncated,
            reconciled,
            ContentHash: string.Empty);

        return sheet with { ContentHash = InvoiceWorksheetCanonical.Hash(sheet) };
    }

    private static WorksheetLedgerSide LedgerSide(
        decimal? total,
        string? currency,
        long rowCount,
        bool truncated = false) =>
        new(total, currency, rowCount, truncated);

    private static ReconciliationUnits Units(
        decimal? total,
        string? currency,
        long rowCount,
        bool truncated = false) =>
        new(
            [
                new ReconciliationUnit(
                    ProjectId,
                    "results",
                    1m,
                    rowCount,
                    total,
                    currency,
                    RollupTotalQuantity: 1m,
                    RollupRowCount: rowCount,
                    RollupTotalAmount: total,
                    RollupCurrency: currency,
                    RollupComputedAt: OccurredAt,
                    Matches: true,
                    Mismatch: null),
            ],
            [],
            truncated);

    private static Evidgate.BuildingBlocks.SharedKernel.Results.Result<StatementDraft> Evaluate(
        InvoiceWorksheet worksheet,
        decimal? total,
        string? currency) =>
        StatementIssueRules.Evaluate(worksheet, new IssueStatementRequest(Period, total, currency));

    private static string? Reason(Evidgate.BuildingBlocks.SharedKernel.Results.Result<StatementDraft> result)
    {
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Status.ShouldBe(422);

        return result.Error.Details![0].Reason;
    }
}
