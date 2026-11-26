using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

public sealed class RedactionInputValueBoundaryTests
{
    private static readonly string PrivateValue = string.Concat("input", "-private-text");
    private static readonly RedactionPipeline Pipeline = new();

    [Theory]
    [InlineData("fill")]
    [InlineData("type")]
    [InlineData("pressSequentially")]
    public void Redact_LocatorInputMethod_MasksItsSingleValueArgument(string method)
    {
        var benign = "await page.locator('#field').describe('" + PrivateValue + "');";
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);
        var payload = "await page.locator('#field')." + method + "('" + PrivateValue + "');";

        var outcome = Pipeline.Redact(payload);

        outcome.Text.ShouldNotContain(PrivateValue);
        (outcome.Blocked || outcome.Report.Findings.Any(finding => finding.RuleId == "input_value"))
            .ShouldBeTrue();
    }

    [Fact]
    public void Redact_UnquotedHtmlInputValue_MasksTheValue()
    {
        var benign = "<input label=" + PrivateValue + ">";
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var outcome = Pipeline.Redact("<input value=" + PrivateValue + ">");

        outcome.Text.ShouldNotContain(PrivateValue);
        (outcome.Blocked || outcome.Report.Findings.Any(finding => finding.RuleId == "input_value"))
            .ShouldBeTrue();
    }

    public static TheoryData<string, string> SingleArgumentSurfaces => new()
    {
        { "await page.getByLabel('Card').fill('", "');" },
        { "await page.getByLabel('Card').fill(\"", "\");" },
        { "await page.getByLabel('Card').fill(`", "`);" },
        { "await page.getByLabel('Card').fill('", "', { timeout: 100 });" },
        { "await page.keyboard.type('", "');" },
        { "await page.keyboard.type(\"", "\");" },
        { "await page.keyboard.type(`", "`);" },
        { "await page.locator('#card').type('", "');" },
        { "await page.locator('#card').pressSequentially('", "');" },
    };

    [Theory]
    [MemberData(nameof(SingleArgumentSurfaces))]
    public void Redact_InputCall_MasksOnlyItsValue(string prefix, string suffix)
    {
        var card = string.Concat("4111 1111 ", "1111 1111");
        var control = Pipeline.Redact("await page.getByLabel('Card').describe('" + card + "');");
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldContain(card);

        var outcome = Pipeline.Redact(prefix + card + suffix);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldNotContain(card);
        outcome.Text.ShouldStartWith(prefix[..prefix.LastIndexOf('(')]);
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "input_value");
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("type")]
    [InlineData("pressSequentially")]
    public void Redact_TwoArgumentInputCall_PreservesTheSelector(string method)
    {
        var payload = "await page." + method + "('#card-field', '" + PrivateValue + "');";

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldContain("'#card-field'");
        outcome.Text.ShouldNotContain(PrivateValue);
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "input_value");
    }

    [Theory]
    [InlineData("<input VALUE=\"", "\">")]
    [InlineData("<input VaLuE='", "'>")]
    [InlineData("<input VALUE=", " data-kind=card>")]
    [InlineData("{\"Value\":\"", "\",\"label\":\"Card\"}")]
    public void Redact_InputValueNameCase_DoesNotChangeMasking(string prefix, string suffix)
    {
        var control = Pipeline.Redact("<input title=\"" + PrivateValue + "\">");
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldContain(PrivateValue);

        var outcome = Pipeline.Redact(prefix + PrivateValue + suffix);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldNotContain(PrivateValue);
        outcome.Text.ShouldEndWith(suffix);
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "input_value");
    }

    public static TheoryData<string, string> MarkerInputSurfaces => new()
    {
        { "<input value=\"", "\">" },
        { "<input value='", "'>" },
        { "{\"value\":\"", "\"}" },
        { "await page.fill('#field', '", "');" },
        { "<input value=", ">" },
        { "await page.fill('#field', \"", "\");" },
        { "await page.fill('#field', `", "`);" },
        { "await page.locator('#field').fill('", "');" },
        { "await page.keyboard.type(\"", "\");" },
        { "await page.locator('#field').pressSequentially(`", "`);" },
    };

    [Theory]
    [MemberData(nameof(MarkerInputSurfaces))]
    public void Redact_InputValueWithMarkerPrefix_DoesNotExemptTheFollowingPrivateValue(string prefix, string suffix)
    {
        const string marker = "[redacted:input_value]";
        var benign = prefix + marker + suffix;
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var outcome = Pipeline.Redact(prefix + marker + PrivateValue + suffix);

        outcome.Text.ShouldNotContain(PrivateValue);
        (outcome.Blocked || outcome.Report.Findings.Any(finding => finding.RuleId == "input_value"))
            .ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(MarkerInputSurfaces))]
    public void Redact_FakeMarkerPrefix_DoesNotExemptTheValue(string prefix, string suffix)
    {
        const string generatedMarker = "[redacted:input_value]";
        var control = Pipeline.Redact(prefix + generatedMarker + suffix);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(prefix + generatedMarker + suffix);
        var card = string.Concat("4111 1111 ", "1111 1111");

        var outcome = Pipeline.Redact(prefix + "[redacted:fake]" + card + suffix);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldNotContain(card);
        outcome.Text.ShouldNotContain("[redacted:fake]");
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "input_value");
    }

    [Theory]
    [MemberData(nameof(MarkerInputSurfaces))]
    public void Redact_UnknownMarkerAlone_IsStillAnInputValue(string prefix, string suffix)
    {
        var outcome = Pipeline.Redact(prefix + "[redacted:fake]" + suffix);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldNotContain("[redacted:fake]");
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "input_value");
    }
}
