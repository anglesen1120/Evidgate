using System.Text;
using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// A value of a class the catalogue masks must not reach the provider because it was spelled with invisible characters,
/// fullwidth look-alikes or an encoding: it is masked, or the payload is refused.
/// </summary>
public sealed class RedactionNormalisationTests
{
    private static readonly string Value = string.Concat("hunter2", "hunter2");
    private static readonly string BearerToken = string.Concat("Q7w8E9r0T1y2U3i4", "O5p6A7s8D9f0G1h2");
    private static readonly string KeyBody = string.Concat("Q7W8E9R0", "T1Y2U3I4");
    private static readonly string SecretKeyBody = string.Concat("Q7w8E9r0T1y2U3i4", "O5p6A7s8");
    private static readonly string PemBody = string.Concat("MIIEvQIBADANBgkq", "hkiG9w0BAQEFAASC");
    private static readonly string SessionPair = string.Concat("sessionid=", "Q7w8E9r0T1y2U3i4");
    private static readonly RedactionPipeline Pipeline = new();

    public static TheoryData<string, string, string> Evasions => new()
    {
        { "credential, fullwidth keyword and separator", Fullwidth("password=") + Value, Fullwidth("document=") + Value },
        { "credential, fullwidth separator", "password\uFF1D" + Value, "document\uFF1D" + Value },
        { "bearer, fullwidth keyword", Fullwidth("Bearer") + " " + BearerToken, Fullwidth("Beaver") + " " + BearerToken },
        { "bearer, fullwidth keyword and token", Fullwidth("Bearer " + BearerToken), Fullwidth("Beaver " + BearerToken) },
        { "bearer, zero-width inside keyword", "Bea\u200Brer " + BearerToken, "Bea\u200Bver " + BearerToken },
        { "email, fullwidth at sign", "casey\uFF20example.test", "casey\uFF3Fexample.test" },
        { "email, fullwidth address", Fullwidth("casey@example.test"), Fullwidth("casey_example.test") },
        { "email, zero-width before at sign", "casey\u200B@example.test", "casey\u200B_example.test" },
        { "phone, fullwidth number", Fullwidth("+84 912 345 678"), Fullwidth("ticket 84 912 345 678") },
        { "input value, fullwidth name", Fullwidth("value=") + "\"typed-text\"", Fullwidth("label=") + "\"typed-text\"" },
        { "input value, fullwidth separator", "value\uFF1D\"typed-text\"", "label\uFF1D\"typed-text\"" },
        { "api key shape, fullwidth prefix", Fullwidth("AKIA") + KeyBody, Fullwidth("AKIB") + KeyBody },
        { "api key shape, zero-width inside", "AKIA\u200B" + KeyBody, "AKIB\u200B" + KeyBody },
        { "api key shape, fullwidth hyphen", "sk\uFF0D" + SecretKeyBody, "sx\uFF0D" + SecretKeyBody },
        {
            "private key, fullwidth header",
            Fullwidth("-----BEGIN PRIVATE KEY-----") + "\n" + PemBody,
            Fullwidth("-----BEGIN CERTIFICATE-----") + "\n" + PemBody
        },
        { "cookie, fullwidth header", Fullwidth("Cookie:") + " " + SessionPair, Fullwidth("Accept:") + " " + SessionPair },
        { "cookie, zero-width inside name", "Coo\u200Bkie: " + SessionPair, "Coo\u200Bkbook: " + SessionPair },
    };

    public static TheoryData<int> InvisibleCharacters
    {
        get
        {
            var data = new TheoryData<int>();
            var codePoints = Enumerable.Range(0x200B, 5)
                .Concat(Enumerable.Range(0x202A, 5))
                .Concat(Enumerable.Range(0x2060, 5))
                .Concat(Enumerable.Range(0x2066, 4));
            foreach (var codePoint in codePoints)
            {
                data.Add(codePoint);
            }

            data.Add(0x00AD);
            data.Add(0x180E);
            data.Add(0xFEFF);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Evasions))]
    public void Redact_NormalisedFormOfAMaskedClass_BlocksEgress(string name, string payload, string control)
    {
        var benign = Pipeline.Redact(control);
        benign.Blocked.ShouldBeFalse(name);
        benign.Text.ShouldBe(control, name);
        benign.Report.Findings.ShouldBeEmpty(name);

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeTrue(name);
        outcome.Text.ShouldBeEmpty(name);
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_normalised", name);
    }

    [Theory]
    [MemberData(nameof(InvisibleCharacters))]
    public void Redact_InvisibleCharacterInsideCredentialKeyword_BlocksEgress(int codePoint)
    {
        var invisible = ((char)codePoint).ToString();
        var benign = "docu" + invisible + "ment=" + Value;
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var outcome = Pipeline.Redact("pass" + invisible + "word=" + Value);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_normalised");
    }

    [Theory]
    [InlineData("\u212Aey=")]
    [InlineData("to\u212Aen=")]
    [InlineData("pa\u017F\u017Fword=")]
    [InlineData("\u017Fecret=")]
    public void Redact_CompatibilityLetterInsideCredentialKeyword_IsMaskedOrBlocked(string assignment)
    {
        var plain = assignment.Replace("\u212A", "x", StringComparison.Ordinal)
            .Replace("\u017F", "x", StringComparison.Ordinal);
        var control = Pipeline.Redact(plain + Value);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldContain(Value);

        var outcome = Pipeline.Redact(assignment + Value);

        outcome.Text.ShouldNotContain(Value);
        (outcome.Blocked || outcome.Report.Findings.Any(finding => finding.RuleId == "credential_assignment"))
            .ShouldBeTrue();
    }

    [Fact]
    public void Redact_PercentEncodedInvisibleCharacter_BlocksEgress()
    {
        var control = Pipeline.Redact("docu%E2%80%8Bment=" + Value);
        control.Blocked.ShouldBeFalse();

        var outcome = Pipeline.Redact("pass%E2%80%8Bword=" + Value);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_normalised");
    }

    [Fact]
    public void Redact_Base64EncodedInvisibleCharacter_BlocksEgress()
    {
        var control = Pipeline.Redact(Convert.ToBase64String(Encoding.UTF8.GetBytes("docu\u200Bment=" + Value)));
        control.Blocked.ShouldBeFalse();

        var outcome = Pipeline.Redact(Convert.ToBase64String(Encoding.UTF8.GetBytes("pass\u200Bword=" + Value)));

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_normalised");
    }

    [Fact]
    public void Redact_PercentEncodedBearerToken_BlocksEgress()
    {
        var benign = Uri.EscapeDataString("Beaver " + BearerToken);
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var outcome = Pipeline.Redact(Uri.EscapeDataString("Bearer " + BearerToken));

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_urlencoded");
    }

    [Fact]
    public void Redact_Base64EncodedBearerToken_BlocksEgress()
    {
        var benign = Convert.ToBase64String(Encoding.UTF8.GetBytes("Beaver " + BearerToken));
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var outcome = Pipeline.Redact(Convert.ToBase64String(Encoding.UTF8.GetBytes("Bearer " + BearerToken)));

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_base64");
    }

    [Fact]
    public void Redact_LargeFullwidthBenignText_IsPreservedWithoutFindings()
    {
        var payload = new string('\uFF41', 50_000) + "\u3000" + Fullwidth("public step");

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldBe(payload);
        outcome.Report.Findings.ShouldBeEmpty();
    }

    private static string Fullwidth(string ascii) =>
        string.Concat(ascii.Select(value => value switch
        {
            ' ' => '\u3000',
            >= '!' and <= '~' => (char)(value + 0xFEE0),
            _ => value,
        }));
}
