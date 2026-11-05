using System.Text;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The refusals about the document itself: unknown, duplicated, missing or oversized members, text no column can hold,
/// and the cap that keeps a pathological body from producing an unbounded answer.
/// </summary>
public sealed partial class UsagePriceCardTests
{
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
