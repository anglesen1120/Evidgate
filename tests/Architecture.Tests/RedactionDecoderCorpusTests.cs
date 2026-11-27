using System.Diagnostics;
using System.Globalization;
using System.Text;
using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

public sealed class RedactionDecoderCorpusTests
{
    private static readonly string Key = string.Concat("pass", "word");
    private static readonly string Secret = string.Concat("opaque", "-fixture-text");
    private static readonly string Token = string.Concat("Q7w8E9r0T1y2U3i4", "O5p6A7s8D9f0G1h2");
    private static readonly RedactionPipeline Pipeline = new();

    public static TheoryData<string, string, string, string> Evasions => new()
    {
        { "mixed bearer invisibles", "Be\uFE0Farer\u3164" + Token, "Be\uFE0Faver\u3164" + Token, Token },
        { "mixed private key invisibles", Pem("PRIV\uFE0FATE\u3164KEY", Token), Pem("CERTIFICATE", Token), Token },
        { "NUL interleaved keyword", Interleave(Key) + "=" + Secret, Interleave("document") + "=" + Secret, Secret },
        { "plus bearer separator", "Bearer+" + Token, "Beaver+" + Token, Token },
        { "plus authorization header", "Authorization=Bearer+" + Token, "Authorship=Beaver+" + Token, Token },
        { "plus private key separator", Pem("PRIVATE+KEY", Token).Replace("BEGIN ", "BEGIN+", StringComparison.Ordinal), Pem("CERTIFICATE", Token), Token },
        { "JSON carriage return", "Bearer\\r" + Token, "Beaver\\r" + Token, Token },
        { "JSON backspace", "pass\\bword=" + Secret, "docu\\bment=" + Secret, Secret },
        { "JSON form feed", "pass\\fword=" + Secret, "docu\\fment=" + Secret, Secret },
        { "JSON escaped slash inside token", "Bearer " + Token[..4] + "\\/" + Token[4..], "Beaver " + Token[..4] + "\\/" + Token[4..], Token[4..] },
        { "hex byte escape", "pass\\x77ord=" + Secret, "docu\\x6Dent=" + Secret, Secret },
        { "braced Unicode escape", "pass\\u{77}ord=" + Secret, "docu\\u{6D}ent=" + Secret, Secret },
        { "eight digit Unicode escape", "pass\\U00000077ord=" + Secret, "docu\\U0000006Dent=" + Secret, Secret },
        { "hex text", Hex(Key + "=" + Secret), Hex("document=" + Secret), Hex(Secret) },
        { "escaped numeric entity", Escape("pass&#119;ord=" + Secret), Escape("docu&#109;ent=" + Secret), Secret },
        { "entity under percent layer", "pass%26%23119%3Bord=" + Secret, "docu%26%23109%3Bent=" + Secret, Secret },
        { "three percent layers", PercentLayers(Key + "=" + Secret, 3), PercentLayers("document=" + Secret, 3), PercentLayers(Secret, 3) },
    };

    [Theory]
    [MemberData(nameof(Evasions))]
    public void Redact_DecoderCorpus_RemovesSecretBytesOrBlocks(string scenario, string payload, string control, string forbidden)
    {
        var benign = Pipeline.Redact(control);
        benign.Blocked.ShouldBeFalse(scenario);
        benign.Text.ShouldBe(control, scenario);
        ContainsBytes(payload, forbidden).ShouldBeTrue(scenario);

        var outcome = Pipeline.Redact(payload);

        ContainsBytes(outcome.Text, forbidden).ShouldBeFalse(scenario);
        ContainsBytes(outcome.Text, Secret).ShouldBeFalse(scenario);
        (outcome.Blocked || outcome.Report.Findings.Count > 0).ShouldBeTrue(scenario);
        if (outcome.Blocked)
        {
            outcome.Text.ShouldBeEmpty(scenario);
        }
    }

    [Fact]
    public void Redact_EscapedMixedInvisibleBearer_PreservesDetectionBeforeEgress()
    {
        var control = Pipeline.Redact(Escape("Be\uFE0Faver\u3164" + Token));
        control.Blocked.ShouldBeFalse();

        var outcome = Pipeline.Redact(Escape("Be\uFE0Farer\u3164" + Token));

        ContainsBytes(outcome.Text, Token).ShouldBeFalse();
    }

    [Fact]
    public void Redact_EncodingBeyondThreeLayers_BlocksRatherThanLeavingAnUnscannedVariant()
    {
        Pipeline.Redact(PercentLayers("document=" + Secret, 3)).Blocked.ShouldBeFalse();

        var outcome = Pipeline.Redact(PercentLayers(Key + "=" + Secret, 4));

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_scan_truncated");
    }

    [Fact]
    public void Redact_EncodedSecretAcrossAWindowEndWithoutWhitespace_IsScannedWhole()
    {
        var prefix = new string('!', RedactionPipeline.MaxWindowChars - 3);
        var control = Pipeline.Redact(prefix + "docu%6Dent=" + Secret);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(prefix + "docu%6Dent=" + Secret);

        var outcome = Pipeline.Redact(prefix + "pass%77ord=" + Secret);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_urlencoded");
    }

    [Theory]
    [InlineData("Bearer", true)]
    [InlineData("Beaver", false)]
    public void Redact_EncodedTokenAfterAKeywordAtTheWindowEnd_IsScannedWithItsKeyword(string keyword, bool blocked)
    {
        var filler = string.Concat(Enumerable.Repeat("ab ", RedactionPipeline.MaxWindowChars / 3))[..(RedactionPipeline.MaxWindowChars - 7)];
        var payload = filler + keyword + " %51" + Token[1..];

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBe(blocked);
        outcome.Text.ShouldBe(blocked ? string.Empty : payload);
    }

    [Theory]
    [InlineData(49146, "password=", true)]
    [InlineData(49146, "document=", true)]
    [InlineData(30000, "document=", false)]
    public void Redact_Base64BlobLongerThanAWindow_IsRefusedNotCutInsideAToken(int padding, string key, bool refused)
    {
        var blob = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('A', padding) + key + Secret));

        var outcome = Pipeline.Redact(blob);

        outcome.Blocked.ShouldBe(refused);
        if (refused)
        {
            outcome.Report.Findings.ShouldContain(finding => finding.RuleId == "residual_window_seam");
        }
        else
        {
            outcome.Text.ShouldBe(blob);
        }
    }

    [Theory]
    [InlineData("mixed decoders")]
    [InlineData("hex run")]
    [InlineData("escape near misses")]
    public void Redact_NewDecoderWorstCase64KiB_FinishesWithinTheExistingDeadline(string shape)
    {
        var unit = shape switch
        {
            "hex run" => "616263646566",
            "escape near misses" => "\\u{FFFFFF}\\UFFFFFFFF\\xG0&amper;+%zz",
            _ => "docu\\u{6D}ent%3Dpublic+&#x3164;\\r\\/616263646566;",
        };
        var payload = string.Concat(Enumerable.Repeat(unit, (65536 / unit.Length) + 1))[..65536];
        Pipeline.Redact("warm up");
        var watch = Stopwatch.StartNew();

        var outcome = Pipeline.Redact(payload);

        watch.Stop();
        outcome.Report.Findings.ShouldNotContain(finding => finding.RuleId == "regex_timeout"
            || finding.RuleId == "redaction_budget_exceeded");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    private static bool ContainsBytes(string text, string value) =>
        Encoding.UTF8.GetBytes(text).AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;

    private static string Interleave(string value) => string.Join('\0', value.ToCharArray());

    private static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private static string Pem(string kind, string body) => string.Concat("-----", "BEGIN ", kind, "-----\n", body);

    private static string Escape(string value)
    {
        var builder = new StringBuilder();
        PromptTextEscaper.AppendEscaped(builder, value);
        return builder.ToString();
    }

    private static string PercentLayers(string value, int layers)
    {
        for (var layer = 0; layer < layers; layer++)
        {
            value = string.Concat(value.Select(c => "%" + ((int)c).ToString("X2", CultureInfo.InvariantCulture)));
        }

        return value;
    }
}
