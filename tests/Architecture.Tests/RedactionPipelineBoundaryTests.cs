using System.Text;
using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

public sealed class RedactionPipelineBoundaryTests
{
    private static readonly string Password = string.Concat("boundary", "-credential");
    private static readonly string BearerToken = string.Concat("Q7w8E9r0T1y2U3i4", "O5p6A7s8D9f0G1h2");
    private static readonly RedactionPipeline Pipeline = new();

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    public void Redact_ShortBase64Credential_BlocksEgress(int valueLength)
    {
        var value = new string('q', valueLength);
        var benign = Convert.ToBase64String(Encoding.UTF8.GetBytes("document=" + value));
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("password=" + value));
        var outcome = Pipeline.Redact(encoded);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_base64");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Redact_NestedBase64WithinDecodeDepth_BlocksEgress(int layers)
    {
        var benign = "document=" + Password;
        var encoded = "password=" + Password;
        for (var layer = 0; layer < layers; layer++)
        {
            benign = Convert.ToBase64String(Encoding.UTF8.GetBytes(benign));
            encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(encoded));
        }

        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);
        var outcome = Pipeline.Redact(encoded);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_base64");
    }

    [Fact]
    public void Redact_UnpaddedBase64UrlCredential_BlocksEgress()
    {
        var benign = Convert.ToBase64String(Encoding.UTF8.GetBytes("document=" + Password))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("password=" + Password))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var outcome = Pipeline.Redact(encoded);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_base64");
    }

    [Fact]
    public void Redact_DoublePercentEncodedCredential_BlocksEgress()
    {
        var benign = Uri.EscapeDataString(Uri.EscapeDataString("document=" + Password));
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);
        var encoded = Uri.EscapeDataString(Uri.EscapeDataString("password=" + Password));

        var outcome = Pipeline.Redact(encoded);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_urlencoded");
    }

    [Fact]
    public void Redact_AllCharactersUnicodeEscaped_BlocksEgress()
    {
        var benign = string.Concat(("document=" + Password).Select(value => $"\\u{(int)value:x4}"));
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);
        var encoded = string.Concat(("password=" + Password).Select(value => $"\\u{(int)value:x4}"));

        var outcome = Pipeline.Redact(encoded);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_unicode_escape");
    }

    [Fact]
    public void Redact_Base64OfInvalidUtf8_PreservesOpaqueText()
    {
        var encoded = Convert.ToBase64String(Enumerable.Repeat((byte)0xff, 30).ToArray());

        var outcome = Pipeline.Redact("opaque " + encoded + " end");

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldBe("opaque " + encoded + " end");
        outcome.Report.Findings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Redact_QuotedCredentialAcrossLines_MasksEveryLine(string lineBreak)
    {
        var first = string.Concat("first", "-part");
        var second = string.Concat("second", "-part");
        var value = first + lineBreak + second;
        var benign = "label=\"" + value + "\"\npublic step";
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var outcome = Pipeline.Redact("password=\"" + value + "\"\npublic step");

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldNotContain(first);
        outcome.Text.ShouldNotContain(second);
        outcome.Text.ShouldEndWith("\npublic step");
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "credential_assignment");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Redact_BearerSeparatedFromValueByLineBreak_MasksTheValue(string lineBreak)
    {
        var benign = "public" + lineBreak + BearerToken;
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var outcome = Pipeline.Redact("Bearer" + lineBreak + BearerToken);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldNotContain(BearerToken);
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "bearer_token");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    public void Redact_WindowCutAfterBearerKeyword_DoesNotExposeTheShortToken(int offset)
    {
        var filler = new string('.', RedactionPipeline.MaxWindowChars - " Bearer ".Length + offset);
        var control = Pipeline.Redact(filler + "\nBearer " + BearerToken + "\npublic step");
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldNotContain(BearerToken);

        var outcome = Pipeline.Redact(filler + " Bearer " + BearerToken + "\npublic step");

        outcome.Text.ShouldNotContain(BearerToken);
        (outcome.Blocked || outcome.Report.Findings.Any(finding => finding.RuleId == "bearer_token"))
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Redact_BenignTextAroundWindowLength_PreservesEveryCharacter(int offset)
    {
        var payload = new string('.', RedactionPipeline.MaxWindowChars + offset);

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldBe(payload);
        outcome.Report.Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Redact_LargeBenignMultilinePayload_PreservesEveryCharacter()
    {
        var payload = string.Concat(Enumerable.Repeat("public cart step completed.\n", 8000));
        payload.Length.ShouldBeGreaterThan(3 * RedactionPipeline.MaxWindowChars);

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldBe(payload);
        outcome.Report.Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Redact_EmptyPayload_HasAStableEmptyReport()
    {
        var first = Pipeline.Redact(string.Empty);
        var second = Pipeline.Redact(string.Empty);

        first.Blocked.ShouldBeFalse();
        first.Text.ShouldBeEmpty();
        first.Report.Findings.ShouldBeEmpty();
        first.Report.RuleVersion.ShouldBe(RedactionRules.Version);
        first.Report.Hash.ShouldBe(second.Report.Hash);
        RedactionReport.Matches(first.Report.ToJson(), first.Report.Hash).ShouldBeTrue();
    }

    [Fact]
    public void Redact_WhitespaceOnlyPayload_PreservesTheBytes()
    {
        const string payload = " \t\r\n\n ";

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldBe(payload);
        outcome.Report.Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Redact_JsonStringContainingEscapedCredential_BlocksEgress()
    {
        var control = Pipeline.Redact("\"postData\":\"{\\\"document\\\":\\\"" + Password + "\\\"}\"");
        control.Blocked.ShouldBeFalse();
        var outcome = Pipeline.Redact("\"postData\":\"{\\\"password\\\":\\\"" + Password + "\\\"}\"");

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_json_escape");
    }

    [Theory]
    [InlineData("&amp;")]
    [InlineData("&#38;")]
    [InlineData("&#x26;")]
    public void Redact_HtmlEntityInsideCredential_BlocksTheSurvivingTail(string entity)
    {
        var value = string.Concat("P", entity, "ssw0rd");
        var control = Pipeline.Redact("document=" + value);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe("document=" + value);

        var outcome = Pipeline.Redact("password=" + value);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_html_entity");
    }

    [Fact]
    public void Redact_Base64SuccessfulTokenBudget_BlocksOnlyWhenTruncated()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("document=public"));
        var control = Pipeline.Redact(string.Join(" ", Enumerable.Repeat(token, 256)));
        control.Blocked.ShouldBeFalse();

        var outcome = Pipeline.Redact(string.Join(" ", Enumerable.Repeat(token, 257)));

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_scan_truncated");
    }

    [Fact]
    public void Redact_InvalidBase64Candidates_DoNotExhaustSuccessfulTokenBudget()
    {
        var invalid = Convert.ToBase64String(Enumerable.Repeat((byte)0xff, 24).ToArray());
        var prefix = string.Join(" ", Enumerable.Repeat(invalid, 300)) + " ";
        var control = Pipeline.Redact(prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes("document=public")));
        control.Blocked.ShouldBeFalse();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("password=" + Password));

        var outcome = Pipeline.Redact(prefix + encoded);

        outcome.Blocked.ShouldBeTrue();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_base64");
        outcome.Report.Findings.ShouldNotContain(finding => finding.RuleId == "residual_scan_truncated");
    }

    [Fact]
    public void Redact_Base64TokenBudget_IsPerWindowNotPerPayload()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("document=public"));
        var part = string.Join(" ", Enumerable.Repeat(token, 256));
        var payload = part + "\n" + new string('.', RedactionPipeline.MaxWindowChars - part.Length - 1) + part;

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldBe(payload);
        outcome.Report.Findings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(106, "", ' ', 200, "Bearer", true)]
    [InlineData(106, "", ' ', 200, "Beaver", false)]
    [InlineData(400, " ", '\n', 600, "Bearer", true)]
    [InlineData(400, " ", '\n', 600, "Beaver", false)]
    public void Redact_KeywordAndValueSeparatedAcrossTheWindowEnd_MasksTheValue(
        int fillerShortfall,
        string lead,
        char gap,
        int gapLength,
        string keyword,
        bool masked)
    {
        var filler = new string('.', RedactionPipeline.MaxWindowChars - fillerShortfall);
        var payload = filler + lead + keyword + new string(gap, gapLength) + BearerToken + "\npublic step";

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        if (masked)
        {
            outcome.Text.ShouldNotContain(BearerToken);
            outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "bearer_token");
        }
        else
        {
            outcome.Text.ShouldBe(payload);
            outcome.Report.Findings.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("passwore", false)]
    public void Redact_QuotedMultilineValueOpenedBeforeTheWindowEnd_IsMaskedToItsClosingQuote(string keyword, bool masked)
    {
        var tail = string.Concat("private", "-tail");
        var filler = new string('.', RedactionPipeline.MaxWindowChars - 400);
        var payload = filler + " " + keyword + "=\"" + string.Concat(Enumerable.Repeat("q\n", 210)) + tail + "\"";

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        if (masked)
        {
            outcome.Text.ShouldNotContain(tail);
            outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "credential_assignment");
        }
        else
        {
            outcome.Text.ShouldBe(payload);
            outcome.Report.Findings.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("passwore", false)]
    public void Redact_QuotedValueSpanningEveryCandidateCut_RefusesTheWindow(string keyword, bool refused)
    {
        var payload = keyword + "=\"" + string.Concat(Enumerable.Repeat("q\n", 45000)) + "tail\"";

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBe(refused);
        if (refused)
        {
            outcome.Text.ShouldBeEmpty();
            outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_window_seam");
        }
        else
        {
            outcome.Text.ShouldBe(payload);
            outcome.Report.Findings.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(12)]
    public void Redact_DeadlineExpiresInsideFinalWindow_BlocksEgress(int allowedReads)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("document=public"));
        var payload = string.Join(" ", Enumerable.Repeat(token, 32));
        Pipeline.Redact(payload).Blocked.ShouldBeFalse();
        var pipeline = new RedactionPipeline(new ExpiringTimeProvider(allowedReads));

        var outcome = pipeline.Redact(payload);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "redaction_budget_exceeded");
    }

    private sealed class ExpiringTimeProvider(int allowedReads) : TimeProvider
    {
        private int _reads;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            Interlocked.Increment(ref _reads) > allowedReads ? RedactionPipeline.Budget.Ticks + 1 : 0;
    }
}
