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

    // ------------------------------------------------------------------ text the database cannot store

    [Fact]
    public void Parse_LoneSurrogateInAValue_RefusedWithoutThrowing()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse(
            "{\"rate_card_version\":\"\\ud800\",\"prices\":[" + Row() + "]}",
            UsagePriceRefusalReasons.InvalidValue,
            "/rate_card_version");
        ShouldRefuse(
            Card(Row(metric: "\"\\ud800\"")),
            UsagePriceRefusalReasons.InvalidValue,
            "/prices/0/metric");
        ShouldRefuse(
            Card(Row(currency: "\"\\ud800\"")),
            UsagePriceRefusalReasons.InvalidValue,
            "/prices/0/currency");
        ShouldRefuse(
            Card(Row(from: "\"\\ud800\"")),
            UsagePriceRefusalReasons.InvalidValue,
            "/prices/0/effective_from");
        ShouldRefuse(
            Card(Row(unitPrice: "\"\\ud800\"")),
            UsagePriceRefusalReasons.InvalidAmount,
            "/prices/0/unit_price");
        ShouldRefuse(
            Card(Row(quota: "\"\\ud800\"")),
            UsagePriceRefusalReasons.InvalidAmount,
            "/prices/0/included_quota");
    }

    [Fact]
    public void Parse_NullCharacterInAValue_RefusedAsInvalidValue()
    {
        ShouldParse(Card(Row()));

        ShouldRefuse(
            "{\"rate_card_version\":\"a\\u0000b\",\"prices\":[" + Row() + "]}",
            UsagePriceRefusalReasons.InvalidValue,
            "/rate_card_version");
        ShouldRefuse(
            Card(Row(metric: "\"res\\u0000ults\"")),
            UsagePriceRefusalReasons.InvalidValue,
            "/prices/0/metric");
    }

    [Fact]
    public void Parse_UnstorableMemberName_RefusedWithoutRepeatingTheName()
    {
        ShouldParse(Card(Row()));

        var root = Parse("{\"rate_card_version\":" + Version + ",\"prices\":[" + Row() + "],\"\\ud800\":1}");
        root.Card.ShouldBeNull();
        root.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.InvalidValue && violation.Path == "/<unknown>");

        var row = Parse(Card(Row(extra: ",\"\\ud800\":1")));
        row.Card.ShouldBeNull();
        row.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.InvalidValue
            && violation.Path == "/prices/0/<unknown>");
    }

    [Fact]
    public void Parse_SurrogatePairInAValue_IsStorableTextAndParses()
    {
        // A valid pair is ordinary text: the guard must refuse lone halves, not every non-BMP character.
        var card = ShouldParse("{\"rate_card_version\":\"2026-\\ud83c\\udf89\",\"prices\":[" + Row() + "]}");

        card.RateCardVersion.ShouldBe("2026-\U0001F389");
    }

    // ------------------------------------------------------------------ bounded refusals

    [Fact]
    public void Parse_MoreRefusalsThanTheCap_StopsTheWalkAndNamesTheOverflow()
    {
        // The largest card, every row naming eight members the card does not declare: thousands of refusals.
        var rows = Enumerable.Range(0, UsagePriceCardRules.MaxRows)
            .Select(_ => Row(extra: string.Concat(Enumerable.Range(0, 8).Select(index => ",\"x" + index + "\":0"))))
            .ToArray();

        var result = Parse(Card(rows));

        result.Card.ShouldBeNull();
        result.Violations.Count.ShouldBe(UsagePriceCardRules.MaxViolations + 1);
        result.Violations[^1].Reason.ShouldBe(UsagePriceRefusalReasons.TooManyViolations);
        result.Violations[^1].Path.ShouldBe("/");
    }

    [Fact]
    public void Parse_MemberNameAtThePathLimit_IsTheLongestNameARefusalRepeats()
    {
        var atLimit = new string('m', 64);

        var echoed = Parse("{\"rate_card_version\":" + Version + ",\"prices\":[" + Row() + "],\"" + atLimit + "\":1}");
        echoed.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.UnknownField && violation.Path == "/" + atLimit);

        var overLimit = new string('m', 65);
        var bounded = Parse("{\"rate_card_version\":" + Version + ",\"prices\":[" + Row() + "],\"" + overLimit + "\":1}");
        bounded.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.UnknownField && violation.Path == "/<unknown>");
    }

    [Fact]
    public void Parse_VeryLongMemberName_RefusedWithABoundedPath()
    {
        var name = new string('n', 200 * 1024);

        var result = Parse("{\"rate_card_version\":" + Version + ",\"prices\":[" + Row() + "],\"" + name + "\":1}");

        result.Card.ShouldBeNull();
        var violation = result.Violations.ShouldHaveSingleItem();
        violation.Reason.ShouldBe(UsagePriceRefusalReasons.UnknownField);
        violation.Path.ShouldBe("/<unknown>");

        var rowResult = Parse(Card(Row(extra: ",\"" + name + "\":1")));
        rowResult.Violations.ShouldContain(inner =>
            inner.Reason == UsagePriceRefusalReasons.UnknownField && inner.Path == "/prices/0/<unknown>");
    }

    [Fact]
    public void Parse_MemberNameFillingTheWholeBody_RefusedWithABoundedPath()
    {
        // The largest name that still parses: everything but the fixed parts of the document. A name that filled the
        // cap by itself could never reach the walk, which is why the body cap is checked first.
        var prefix = "{\"rate_card_version\":" + Version + ",\"prices\":[" + Row() + "],\"";
        const string suffix = "\":1}";
        var name = new string('n', UsagePriceCardRules.MaxBodyBytes - Encoding.UTF8.GetByteCount(prefix + suffix));

        var result = Parse(prefix + name + suffix);

        result.Card.ShouldBeNull();
        var violation = result.Violations.ShouldHaveSingleItem();
        violation.Reason.ShouldBe(UsagePriceRefusalReasons.UnknownField);
        violation.Path.ShouldBe("/<unknown>");
    }

    [Fact]
    public void Parse_MemberNameAsLongAsTheBodyCap_RefusedWithoutEchoingIt()
    {
        var name = new string('n', UsagePriceCardRules.MaxBodyBytes);

        var result = Parse("{\"" + name + "\":1}");

        result.Card.ShouldBeNull();
        var violation = result.Violations.ShouldHaveSingleItem();
        violation.Reason.ShouldBe(UsagePriceRefusalReasons.TooLarge);
        violation.Path.ShouldBe("/");
    }

    [Fact]
    public void Parse_FiftyThousandUnknownMembers_AnswersInBoundedSize()
    {
        // Fifty thousand members do not fit the body cap, so the whole body is refused before the walk; the answer
        // stays one refusal rather than listing members.
        var members = string.Concat(Enumerable.Range(0, 50_000).Select(index => ",\"m" + index + "\":0"));

        var result = Parse("{\"rate_card_version\":" + Version + ",\"prices\":[" + Row() + "]" + members + "}");

        result.Card.ShouldBeNull();
        var violation = result.Violations.ShouldHaveSingleItem();
        violation.Reason.ShouldBe(UsagePriceRefusalReasons.TooLarge);
        violation.Path.ShouldBe("/");
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
