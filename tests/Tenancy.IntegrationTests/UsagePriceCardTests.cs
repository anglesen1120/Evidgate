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
public sealed class UsagePriceCardTests
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

    // ------------------------------------------------------------------ shape of the document

    [Fact]
    public void Parse_UnknownMetric_Refused()
    {
        ShouldParse(Card(Row(metric: "\"results\"")));

        ShouldRefuse(
            Card(Row(metric: "\"events\"")),
            UsagePriceRefusalReasons.UnknownMetric,
            "/prices/0/metric");
    }

    [Fact]
    public void Parse_UnknownRootMember_Refused()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse(
            "{\"rate_card_version\":" + Version + ",\"prices\":[" + Row() + "],\"discount\":1}",
            UsagePriceRefusalReasons.UnknownField,
            "/discount");
    }

    [Fact]
    public void Parse_UnknownPriceMember_Refused()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse(
            Card(Row(extra: ",\"discount\":\"1\"")),
            UsagePriceRefusalReasons.UnknownField,
            "/prices/0/discount");
    }

    [Fact]
    public void Parse_DuplicateRootMember_Refused()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse(
            "{\"rate_card_version\":" + Version + ",\"rate_card_version\":\"2026-12-01\",\"prices\":[" + Row() + "]}",
            UsagePriceRefusalReasons.DuplicateMember,
            "/rate_card_version");
    }

    [Fact]
    public void Parse_DuplicatePriceMember_Refused()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse(
            Card(Row(extra: ",\"unit_price\":\"0.0200\"")),
            UsagePriceRefusalReasons.DuplicateMember,
            "/prices/0/unit_price");
    }

    [Fact]
    public void Parse_MissingRateCardVersion_Refused()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse(
            "{\"prices\":[" + Row() + "]}",
            UsagePriceRefusalReasons.Required,
            "/rate_card_version");
    }

    [Fact]
    public void Parse_MissingUnitPrice_Refused()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse(
            Card("{\"metric\":\"results\",\"currency\":\"USD\",\"effective_from\":\"2026-01-01T00:00:00Z\"}"),
            UsagePriceRefusalReasons.Required,
            "/prices/0/unit_price");
    }

    [Fact]
    public void Parse_RateCardVersionLongerThanTheColumn_Refused()
    {
        ShouldParse(Card(Row()));

        var version = new string('v', UsagePriceCardRules.MaxVersionLength + 1);
        ShouldRefuse(
            "{\"rate_card_version\":\"" + version + "\",\"prices\":[" + Row() + "]}",
            UsagePriceRefusalReasons.TooLong,
            "/rate_card_version");
    }

    [Fact]
    public void Parse_EmptyPrices_Refused()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse(
            "{\"rate_card_version\":" + Version + ",\"prices\":[]}",
            UsagePriceRefusalReasons.Empty,
            "/prices");
    }

    [Fact]
    public void Parse_MoreRowsThanOneLoadAccepts_Refused()
    {
        ShouldParse(Card(Row()));

        var rows = Enumerable.Repeat(Row(), UsagePriceCardRules.MaxRows + 1).ToArray();
        ShouldRefuse(Card(rows), UsagePriceRefusalReasons.TooMany, "/prices");
    }

    [Fact]
    public void Parse_BodyLargerThanTheCap_Refused()
    {
        ShouldParse(Card(Row()));

        var body = new string(' ', UsagePriceCardRules.MaxBodyBytes + 1);
        ShouldRefuse(body, UsagePriceRefusalReasons.TooLarge, "/");
    }

    [Fact]
    public void Parse_MalformedJson_Refused()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse("{", UsagePriceRefusalReasons.InvalidJson, "/");
    }

    [Fact]
    public void Parse_RootThatIsNotAnObject_Refused()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse("[]", UsagePriceRefusalReasons.InvalidValue, "/");
    }

    // ------------------------------------------------------------------ no echo

    [Fact]
    public void Parse_RefusedValue_NeverEchoesTheInput()
    {
        const string marker = "do-not-echo-9f3a";

        var metric = Parse(Card(Row(metric: "\"" + marker + "\"")));
        metric.Violations.ShouldNotBeEmpty();
        metric.Violations.ShouldAllBe(violation => !Echoes(violation, marker));

        var amount = Parse(Card(Row(unitPrice: "\"" + marker + "\"")));
        amount.Violations.ShouldNotBeEmpty();
        amount.Violations.ShouldAllBe(violation => !Echoes(violation, marker));
    }

    private static bool Echoes(UsagePriceViolation violation, string marker) =>
        violation.Path.Contains(marker, StringComparison.Ordinal)
        || violation.Reason.Contains(marker, StringComparison.Ordinal);
}
