using System.Text;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The rate-card body's rules, without a database: a card is refused whole, each refusal names a stable reason and the
/// JSON path of the offending member, and no refusal carries the value it refused. Money is checked against the raw
/// token, so an exponent, a sign, a separator or a binary floating-point round trip cannot reach the column.
/// <para>
/// Every refusal case keeps a positive control: the same card with only the member under test changed parses, so a test
/// cannot pass because the card shape itself is wrong.
/// </para>
/// </summary>
public sealed partial class UsagePriceCardTests
{
    private const string Version = "\"2026-11-01\"";

    /// <summary>Amount tokens that are not a plain nonnegative decimal, each refused the same way.</summary>
    private static readonly string[] BadAmountTokens = ["\"NaN\"", "\"0.01.0\"", "\"1,5\"", "\"\"", "true"];

    private static string Row(
        string unitPrice = "\"0.0100\"",
        string metric = "\"results\"",
        string currency = "\"USD\"",
        string from = "\"2026-01-01T00:00:00Z\"",
        string to = "null",
        string quota = "null",
        string extra = "") =>
        "{\"metric\":" + metric
        + ",\"unit_price\":" + unitPrice
        + ",\"currency\":" + currency
        + ",\"effective_from\":" + from
        + ",\"effective_to\":" + to
        + ",\"included_quota\":" + quota + extra + "}";

    private static string Card(params string[] rows) =>
        "{\"rate_card_version\":" + Version + ",\"prices\":[" + string.Join(",", rows) + "]}";

    private static UsagePriceCardParseResult Parse(string json) =>
        UsagePriceCardParser.Parse(Encoding.UTF8.GetBytes(json));

    private static UsagePriceCard ShouldParse(string json)
    {
        var result = Parse(json);
        result.Violations.ShouldBeEmpty();
        return result.Card.ShouldNotBeNull();
    }

    private static void ShouldRefuse(string json, string reason, string path)
    {
        var result = Parse(json);
        result.Card.ShouldBeNull();
        result.Violations.ShouldContain(violation => violation.Reason == reason && violation.Path == path);
    }

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public void Parse_ValidCard_KeepsEveryRowInOrder()
    {
        var card = ShouldParse(Card(
            Row(metric: "\"results\""),
            Row(metric: "\"ai_tokens\"", unitPrice: "1.5", to: "\"2027-01-01T00:00:00Z\"", quota: "\"1000\"")));

        card.RateCardVersion.ShouldBe("2026-11-01");
        card.Rows.Count.ShouldBe(2);
        card.Rows[0].Metric.ShouldBe(UsageMetric.Results);
        card.Rows[0].UnitPrice.ShouldBe(0.0100m);
        card.Rows[0].EffectiveTo.ShouldBeNull();
        card.Rows[1].Metric.ShouldBe(UsageMetric.AiTokens);
        card.Rows[1].UnitPrice.ShouldBe(1.5m);
        card.Rows[1].IncludedQuota.ShouldBe(1000m);
    }

    [Fact]
    public void Parse_TwelveFractionDigits_RoundTripsExactly()
    {
        var card = ShouldParse(Card(Row(unitPrice: "\"0.123456789012\"")));

        card.Rows.ShouldHaveSingleItem().UnitPrice.ShouldBe(0.123456789012m);
    }

    [Fact]
    public void Parse_AmountAsJsonNumber_KeepsTheSameDecimal()
    {
        var fromString = ShouldParse(Card(Row(unitPrice: "\"0.0100\"")));
        var fromNumber = ShouldParse(Card(Row(unitPrice: "0.0100")));

        fromString.Rows[0].UnitPrice.ShouldBe(fromNumber.Rows[0].UnitPrice);
        fromNumber.Rows[0].UnitPrice.ShouldBe(0.0100m);
    }

    [Fact]
    public void Parse_AmountAtTheDigitLimits_RoundTripsExactly()
    {
        // Sixteen integer and twelve fraction digits is the widest token the shape accepts: 28 significant digits,
        // which is still inside decimal's exact range.
        var card = ShouldParse(Card(Row(unitPrice: "\"1234567890123456.123456789012\"")));

        card.Rows.ShouldHaveSingleItem().UnitPrice.ShouldBe(1234567890123456.123456789012m);
    }

    [Fact]
    public void Parse_AmountWithLeadingZeros_ComparesOnlyItsSignificantDigits()
    {
        var card = ShouldParse(Card(Row(unitPrice: "\"0000000000000000.000000000001\"")));

        card.Rows.ShouldHaveSingleItem().UnitPrice.ShouldBe(0.000000000001m);
    }

    [Fact]
    public void Parse_AmountWiderThanTheDigitLimits_Refused()
    {
        ShouldParse(Card(Row(unitPrice: "\"1234567890123456\"")));

        // Seventeen integer digits and a twenty-digit quota: both exceed what decimal holds exactly, as string and as
        // a JSON number token, so neither is rounded into the column.
        ShouldRefuse(
            Card(Row(unitPrice: "\"12345678901234567\"")),
            UsagePriceRefusalReasons.InvalidAmount,
            "/prices/0/unit_price");
        ShouldRefuse(
            Card(Row(unitPrice: "12345678901234567")),
            UsagePriceRefusalReasons.InvalidAmount,
            "/prices/0/unit_price");
        ShouldRefuse(
            Card(Row(quota: "\"12345678901234567890\"")),
            UsagePriceRefusalReasons.InvalidAmount,
            "/prices/0/included_quota");
        ShouldRefuse(
            Card(Row(quota: "12345678901234567890")),
            UsagePriceRefusalReasons.InvalidAmount,
            "/prices/0/included_quota");
    }

    // ------------------------------------------------------------------ ranges and versions

    [Fact]
    public void Parse_OverlappingRangesOfOneMetric_Refused()
    {
        ShouldParse(Card(
            Row(metric: "\"results\"", to: "\"2026-03-01T00:00:00Z\""),
            Row(metric: "\"ai_tokens\"", from: "\"2026-02-01T00:00:00Z\"", to: "\"2026-04-01T00:00:00Z\"")));

        ShouldRefuse(
            Card(
                Row(metric: "\"results\"", to: "\"2026-03-01T00:00:00Z\""),
                Row(metric: "\"results\"", from: "\"2026-02-01T00:00:00Z\"", to: "\"2026-04-01T00:00:00Z\"")),
            UsagePriceRefusalReasons.OverlappingRange,
            "/prices/1/metric");
    }

    [Fact]
    public void Parse_RepeatedMetricWhoseRangesTouch_RefusedAsDuplicateVersion()
    {
        ShouldParse(Card(
            Row(metric: "\"results\"", to: "\"2026-02-01T00:00:00Z\""),
            Row(metric: "\"ai_tokens\"", from: "\"2026-02-01T00:00:00Z\"")));

        ShouldRefuse(
            Card(
                Row(metric: "\"results\"", to: "\"2026-02-01T00:00:00Z\""),
                Row(metric: "\"results\"", from: "\"2026-02-01T00:00:00Z\"")),
            UsagePriceRefusalReasons.DuplicateVersion,
            "/prices/1/metric");
    }

    [Fact]
    public void Parse_RangeThatEndsBeforeItStarts_Refused()
    {
        ShouldParse(Card(Row(from: "\"2026-02-01T00:00:00Z\"", to: "\"2026-02-01T00:00:01Z\"")));

        ShouldRefuse(
            Card(Row(from: "\"2026-02-01T00:00:00Z\"", to: "\"2026-02-01T00:00:00Z\"")),
            UsagePriceRefusalReasons.InvalidValue,
            "/prices/0/effective_to");
    }

    [Fact]
    public void Parse_InstantWithoutAnOffset_Refused()
    {
        ShouldParse(Card(Row(from: "\"2026-01-01T00:00:00Z\"")));

        ShouldRefuse(
            Card(Row(from: "\"2026-01-01T00:00:00\"")),
            UsagePriceRefusalReasons.InvalidValue,
            "/prices/0/effective_from");
    }

    [Fact]
    public void Parse_InstantFinerThanAMicrosecond_Refused()
    {
        // timestamptz stores whole microseconds; anything finer would move the stored boundary.
        ShouldParse(Card(Row(
            from: "\"2026-01-01T00:00:00.123456Z\"",
            to: "\"2026-01-01T00:00:01.123456Z\"")));

        ShouldRefuse(
            Card(Row(from: "\"2026-01-01T00:00:00.1234567Z\"")),
            UsagePriceRefusalReasons.InvalidInstant,
            "/prices/0/effective_from");
        ShouldRefuse(
            Card(Row(to: "\"2026-01-31T00:00:00.0000001Z\"")),
            UsagePriceRefusalReasons.InvalidInstant,
            "/prices/0/effective_to");
    }

    [Fact]
    public void Parse_WindowFinerThanAMicrosecond_RefusedRatherThanCollapsed()
    {
        // Both bounds would round to the same microsecond, so the stored window would be empty.
        ShouldRefuse(
            Card(Row(from: "\"2026-01-01T00:00:00.0000001Z\"", to: "\"2026-01-01T00:00:00.0000009Z\"")),
            UsagePriceRefusalReasons.InvalidInstant,
            "/prices/0/effective_from");
        ShouldRefuse(
            Card(Row(from: "\"2026-01-01T00:00:00Z\"", to: "\"2026-01-01T00:00:00.0000001Z\"")),
            UsagePriceRefusalReasons.InvalidInstant,
            "/prices/0/effective_to");

        // One microsecond is the shortest window the column can hold.
        ShouldParse(Card(Row(
            from: "\"2026-01-01T00:00:00.000000Z\"",
            to: "\"2026-01-01T00:00:00.000001Z\"")));
    }

    // ------------------------------------------------------------------ money

    [Fact]
    public void Parse_NegativeAmount_Refused()
    {
        ShouldParse(Card(Row(unitPrice: "\"0.0100\"")));

        ShouldRefuse(Card(Row(unitPrice: "-0.01")), UsagePriceRefusalReasons.InvalidAmount, "/prices/0/unit_price");
        ShouldRefuse(Card(Row(unitPrice: "\"-0.01\"")), UsagePriceRefusalReasons.InvalidAmount, "/prices/0/unit_price");
    }

    [Fact]
    public void Parse_ExponentAmount_Refused()
    {
        ShouldParse(Card(Row(unitPrice: "\"0.01\"")));

        ShouldRefuse(Card(Row(unitPrice: "1e5")), UsagePriceRefusalReasons.InvalidAmount, "/prices/0/unit_price");
        ShouldRefuse(Card(Row(unitPrice: "\"1e5\"")), UsagePriceRefusalReasons.InvalidAmount, "/prices/0/unit_price");
    }

    [Fact]
    public void Parse_AmountThatIsNotAPlainDecimal_Refused()
    {
        ShouldParse(Card(Row(unitPrice: "\"0.01\"")));

        foreach (var token in BadAmountTokens)
        {
            ShouldRefuse(Card(Row(unitPrice: token)), UsagePriceRefusalReasons.InvalidAmount, "/prices/0/unit_price");
        }
    }

    [Fact]
    public void Parse_MoreFractionDigitsThanTheColumnStores_Refused()
    {
        ShouldParse(Card(Row(unitPrice: "\"0.123456789012\"")));

        ShouldRefuse(
            Card(Row(unitPrice: "\"0.1234567890123\"")),
            UsagePriceRefusalReasons.InvalidAmount,
            "/prices/0/unit_price");
    }

    [Fact]
    public void Parse_NegativeIncludedQuota_Refused()
    {
        ShouldParse(Card(Row(quota: "\"1000\"")));

        ShouldRefuse(
            Card(Row(quota: "\"-1\"")),
            UsagePriceRefusalReasons.InvalidAmount,
            "/prices/0/included_quota");
    }

    [Fact]
    public void Parse_CurrencyNotThreeUppercaseLetters_Refused()
    {
        ShouldParse(Card(Row(currency: "\"USD\"")));

        ShouldRefuse(Card(Row(currency: "\"usd\"")), UsagePriceRefusalReasons.InvalidValue, "/prices/0/currency");
        ShouldRefuse(Card(Row(currency: "\"US\"")), UsagePriceRefusalReasons.InvalidValue, "/prices/0/currency");
    }
}
