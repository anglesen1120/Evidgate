using System.Globalization;
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
    private static readonly RedactionPipeline UntimedPipeline = new(new FrozenClock());

    public static TheoryData<string, string, string> Evasions => new()
    {
        { "credential, fullwidth keyword and separator", Fullwidth("password=") + Value, Fullwidth("document=") + Value },
        { "credential, fullwidth separator", "password\uFF1D" + Value, "document\uFF1D" + Value },
        { "credential, zero-width inside keyword", "pass\u200Bword=" + Value, "docu\u200Bment=" + Value },
        { "bearer, fullwidth keyword", Fullwidth("Bearer") + " " + BearerToken, Fullwidth("Beaver") + " " + BearerToken },
        { "bearer, fullwidth keyword and token", Fullwidth("Bearer " + BearerToken), Fullwidth("Beaver " + BearerToken) },
        { "bearer, zero-width inside keyword", "Bea\u200Brer " + BearerToken, "Bea\u200Bver " + BearerToken },
        { "bearer, invisible-only separator", "Bearer\u3164" + BearerToken, "Beaver\u3164" + BearerToken },
        { "bearer, invisible separator run", "Bearer\u200B\u3164" + BearerToken, "Beaver\u200B\u3164" + BearerToken },
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
        {
            "private key, invisible-only separator",
            "-----BEGIN\u3164PRIVATE KEY-----\n" + PemBody,
            "-----BEGIN\u3164CERTIFICATE-----\n" + PemBody
        },
        { "cookie, fullwidth header", Fullwidth("Cookie:") + " " + SessionPair, Fullwidth("Accept:") + " " + SessionPair },
        { "cookie, zero-width inside name", "Coo\u200Bkie: " + SessionPair, "Coo\u200Bkbook: " + SessionPair },
    };

    public static TheoryData<int> InvisibleCharacters
    {
        get
        {
            var data = new TheoryData<int>();
            // Both ends of every range, and a middle one where a range is wide; the supplementary rows are the tag
            // block and the variation selectors supplement, which a char-wise scan never sees.
            var codePoints = Enumerable.Range(0x200B, 5)
                .Concat(Enumerable.Range(0x202A, 5))
                .Concat(Enumerable.Range(0x2060, 10))
                .Concat([0x00AD, 0x034F, 0x061C, 0x115F, 0x1160, 0x17B4, 0x17B5])
                .Concat([0x180B, 0x180C, 0x180D, 0x180E, 0x180F, 0x3164, 0xFE00, 0xFE08, 0xFE0F, 0xFEFF, 0xFFA0])
                .Concat(Enumerable.Range(0xFFF0, 9))
                .Concat([0xE0000, 0xE0001, 0xE0002, 0xE0020, 0xE0041, 0xE007F, 0xE0100, 0xE0180, 0xE01EF, 0xE0FFF, 0x1D173]);
            foreach (var codePoint in codePoints)
            {
                data.Add(codePoint);
            }

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
        var invisible = char.ConvertFromUtf32(codePoint);
        var benign = "docu" + invisible + "ment=" + Value;
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var outcome = Pipeline.Redact("pass" + invisible + "word=" + Value);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_normalised");
    }

    [Fact]
    public void Redact_EveryFormatCharacterInsideCredentialKeyword_BlocksEgress()
    {
        var missed = new List<int>();
        for (var codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            if (codePoint is >= 0xD800 and <= 0xDFFF
                || CharUnicodeInfo.GetUnicodeCategory(codePoint) != UnicodeCategory.Format)
            {
                continue;
            }

            var outcome = Pipeline.Redact("pass" + char.ConvertFromUtf32(codePoint) + "word=" + Value);
            if (!outcome.Blocked || outcome.Text.Length != 0)
            {
                missed.Add(codePoint);
            }
        }

        missed.ShouldBeEmpty();
    }

    [Fact]
    public void Redact_LoneSurrogateBesideInvisibleCharacter_StillBlocksEgress()
    {
        var outcome = Pipeline.Redact("\uD800pass\u200Bword=" + Value);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
    }

    [Fact]
    public void Redact_ThreeThousandBenignBase64ShapedTokens_AreNotBlocked()
    {
        var payload = Tokens(Enumerable.Range(0, 3000)
            .Select(index => Convert.ToBase64String(Encoding.ASCII.GetBytes("n" + index.ToString("D5", CultureInfo.InvariantCulture)))));

        var outcome = UntimedPipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldBe(payload);
        outcome.Report.Findings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(4096, false)]
    [InlineData(4097, true)]
    [InlineData(5000, true)]
    public void Redact_DecodableTokensAgainstTheWindowBudget_BlockOnlyBeyondTheCap(int count, bool blocked)
    {
        var payload = Tokens(Enumerable.Repeat(Convert.ToBase64String("abcdef"u8), count));

        var outcome = UntimedPipeline.Redact(payload);

        outcome.Blocked.ShouldBe(blocked);
        outcome.Text.ShouldBe(blocked ? string.Empty : payload);
        outcome.Report.Findings.Any(finding => finding.RuleId == "residual_scan_truncated").ShouldBe(blocked);
    }

    [Theory]
    [InlineData(1365, false)]
    [InlineData(1366, true)]
    public void Redact_NormalisedVariants_ShareTheWindowDecodeBudget(int count, bool blocked)
    {
        var payload = "\u3164" + Tokens(Enumerable.Repeat(Convert.ToBase64String("abcdef"u8), count));

        var outcome = UntimedPipeline.Redact(payload);

        outcome.Blocked.ShouldBe(blocked);
        outcome.Text.ShouldBe(blocked ? string.Empty : payload);
        outcome.Report.Findings.Any(finding => finding.RuleId == "residual_scan_truncated").ShouldBe(blocked);
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
    public void Redact_PercentEncodedFullwidthBearerWithInvisibleSeparator_BlocksEgress()
    {
        var benign = Uri.EscapeDataString(Fullwidth("Beaver") + "\u3164" + BearerToken);
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);

        var outcome = Pipeline.Redact(Uri.EscapeDataString(Fullwidth("Bearer") + "\u3164" + BearerToken));

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_normalised");
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

    private static string Tokens(IEnumerable<string> tokens) => string.Join(' ', tokens);

    // The decode budget is the subject of these tests, not the 10 s deadline, which a loaded host could trip.
    private sealed class FrozenClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    private static string Fullwidth(string ascii) =>
        string.Concat(ascii.Select(value => value switch
        {
            ' ' => '\u3000',
            >= '!' and <= '~' => (char)(value + 0xFEE0),
            _ => value,
        }));
}
