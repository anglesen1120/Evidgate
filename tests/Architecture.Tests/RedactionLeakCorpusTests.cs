using System.Diagnostics;
using System.Text;
using Evidgate.BuildingBlocks.Observability.Redaction;
using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// One golden row per secret class the catalogue masks, in plain and obfuscated spellings: the bytes that reach the
/// provider hold none of the secret, or the payload is refused. Negative rows prove ordinary text, an already-masked
/// value and a look-alike word are left byte-identical. Pure functions; no database. Secret-looking fixtures are built
/// from fragments so no scanner sees a literal credential in the source.
/// </summary>
public sealed partial class RedactionLeakCorpusTests
{
    private static readonly string Key = string.Concat("pass", "word");
    private static readonly string Secret = string.Concat("opaque", "-fixture-text");
    private static readonly string Token = string.Concat("Q7w8E9r0T1y2U3i4", "O5p6A7s8D9f0G1h2");
    private static readonly RedactionPipeline Pipeline = new();

    // name, payload, bytes that must not reach the provider, rule id the report names.
    public static TheoryData<string, string, string, string> Leaks => new()
    {
        { "credential", Key + "=" + Secret, Secret, "credential_assignment" },
        { "cookie header", "Cookie: sid=" + Secret, Secret, "cookie" },
        { "named cookie", "__Host-sid=" + Secret, Secret, "cookie" },
        { "authorization", "Authorization: Basic " + Token, Token, "authorization_header" },
        { "bearer", "Bearer " + Token, Token, "bearer_token" },
        { "github token", string.Concat("ghp", "_", Token), Token, "token_shape" },
        { "email", string.Concat("fixture", "@example.test"), "fixture@example.test", "email" },
        { "phone", string.Concat("+84 912 ", "345 678"), "912 345 678", "phone" },
        { "api key", string.Concat("sk", "-", Token), Token, "api_key_shape" },
        { "private key", PemHeader("BEGIN") + "\n" + Token + "\n" + PemHeader("END"), Token, "private_key" },
        { "html input", "<input value=\"" + Secret + "\">", Secret, "input_value" },
        { "json input", "{\"value\":\"" + Secret + "\"}", Secret, "input_value" },
        { "fill, literal selector", "page.fill('#card', '" + Secret + "')", Secret, "input_value" },
        { "fill, identifier selector", "page.fill(SEL.a, '" + Secret + "')", Secret, "input_value" },
        { "fill, call selector with comma", "page.fill(selectors.get('a,b'), '" + Secret + "')", Secret, "input_value" },
        { "fill, selector with escaped quote", "page.fill('#a\\'b', '" + Secret + "')", Secret, "input_value" },
        { "fill, trailing comma", "locator.fill('" + Secret + "',)", Secret, "input_value" },
        { "type, identifier selector", "page.type(SEL.a, '" + Secret + "')", Secret, "input_value" },
        { "pressSequentially, identifier selector", "page.pressSequentially(SEL.a, '" + Secret + "')", Secret, "input_value" },
        { "credential, mask then tail", Key + "=*** " + Secret, Secret, "credential_assignment" },
        { "cookie, mask then tail", "__Host-sid=*** " + Secret, Secret, "cookie" },
        { "credential, mask then comma tail", Key + "=***," + Secret, Secret, "credential_assignment" },
        { "credential, mask then bracket tail", Key + "=***)" + Secret, Secret, "credential_assignment" },
        { "credential, mask then mismatched sibling quotes", Key + "=***,\"public'=" + Secret, Secret, "credential_assignment" },
        { "authorization, mask then mismatched sibling quotes", "\"authorization\": ***,\"public':" + Secret, Secret, "authorization_header" },
        { "cookie, mask then bracket tail", "__Host-sid=***)" + Secret, Secret, "cookie" },
        { "authorization, mask then comma tail", "\"authorization\": ***," + Secret, Secret, "authorization_header" },
        { "percent", "pass%77ord%3D" + Secret, Secret, "residual_urlencoded" },
        { "unicode escape", "pass\\u0077ord=" + Secret, Secret, "residual_unicode_escape" },
        { "json escape", "Bearer\\t" + Token, Token, "residual_json_escape" },
        { "base64", Base64(Key + "=" + Secret), Base64(Key + "=" + Secret), "residual_base64" },
        { "nested base64", Base64(Base64(Key + "=" + Secret)), Base64(Base64(Key + "=" + Secret)), "residual_base64" },
        { "zero width", "pass\u200Bword=" + Secret, Secret, "residual_normalised" },
        { "fullwidth", Fullwidth(Key + "=") + Secret, Secret, "residual_normalised" },
        { "html entity", "pass&#119;ord=" + Secret, Secret, "residual_html_entity" },
        { "html entity zero width", "pass&#x200B;word=" + Secret, Secret, "residual_normalised" },
        { "mathematical alphanumerics", Mathematical(Key) + "=" + Secret, Secret, "residual_normalised" },
        { "circled letters", Circled(Key) + "=" + Secret, Secret, "residual_normalised" },
        { "small form separator", Key + "\uFE66" + Secret, Secret, "residual_normalised" },
    };

    [Theory]
    [MemberData(nameof(Leaks))]
    public void Redact_GoldenLeakCorpus_RemovesTheSecretBytesOrBlocks(string name, string payload, string secretBytes, string rule)
    {
        ContainsBytes(payload, secretBytes).ShouldBeTrue(name + ": the control payload must hold the secret");

        var outcome = Pipeline.Redact(payload);

        ContainsBytes(outcome.Text, secretBytes).ShouldBeFalse(name);
        outcome.Report.Findings.ShouldContain(finding => finding.RuleId == rule, name);
        if (outcome.Blocked)
        {
            outcome.Text.ShouldBeEmpty(name);
            return;
        }

        outcome.Text.ShouldNotBeEmpty(name);
        var again = Pipeline.Redact(outcome.Text);
        again.Blocked.ShouldBeFalse(name);
        again.Text.ShouldBe(outcome.Text, name);
        again.Report.Findings.ShouldBeEmpty(name);
    }

    public static TheoryData<string> Benign => new()
    {
        "Step failed: checkout was not visible at cart.spec.ts:42",
        "ordinary words: passwordless, bearer, cookie jar, keyboard",
        "document=" + Secret,
        "pass&#x200B;wore=" + Secret,
        Mathematical("document") + "=" + Secret,
        Circled("document") + "=" + Secret,
        "document\uFE66" + Secret,
        Key + "=***",
        Key + "= \t*** \t;Host=local",
        "__Host-sid=***",
        "__Host-sid= \t*** \t;Path=/",
        "<input value=\"[redacted:input_value]\">",
        "locator.fill('[redacted:input_value]',)",
        Base64("document=" + Secret),
    };

    [Theory]
    [MemberData(nameof(Benign))]
    public void Redact_BenignCorpus_KeepsEveryByteAndDoesNotBlock(string payload)
    {
        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        Encoding.UTF8.GetBytes(outcome.Text).ShouldBe(Encoding.UTF8.GetBytes(payload));
        outcome.Report.Findings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("password")]
    [InlineData("__Host-sid")]
    public void RedactText_MaskFollowedByTail_MasksTheTailAndStaysStable(string key)
    {
        var redactor = new SecretRedactor();
        var masked = key + "=***";
        redactor.RedactText(masked).ShouldBe(masked);

        var result = redactor.RedactText(masked + " " + Secret);

        ContainsBytes(result, Secret).ShouldBeFalse();
        result.ShouldBe(masked);
        redactor.RedactText(result).ShouldBe(result);
    }

    [Theory]
    [InlineData("SEL.a")]
    [InlineData("selectors.get('a,b')")]
    [InlineData("'#a\\'b'")]
    public void Redact_FillSelectorExpression_KeepsTheSelectorAndMasksOnlyTheValue(string selector)
    {
        var outcome = Pipeline.Redact("page.fill(" + selector + ", '" + Secret + "')");

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldBe("page.fill(" + selector + ", \"[redacted:input_value]\")");
    }

    [Theory]
    [InlineData("near-miss prefixes")]
    [InlineData("whitespace run")]
    [InlineData("base64-looking runs")]
    [InlineData("unclosed brackets")]
    [InlineData("unterminated quoted selectors")]
    public void Redact_WorstCase64KiB_StaysLinearAndFinishes(string shape)
    {
        var payload = WorstCase(shape);
        payload.Length.ShouldBe(65536);
        Pipeline.Redact("warm up");
        var watch = Stopwatch.StartNew();

        var outcome = Pipeline.Redact(payload);

        watch.Stop();
        // A regex that backtracks quadratically is cut off by its timeout and shows as one of these two findings.
        outcome.Report.Findings.ShouldNotContain(finding => finding.RuleId == "regex_timeout" || finding.RuleId == "redaction_budget_exceeded", shape);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), shape);
    }

    [Fact]
    public void FindCut_DeadlineSpentDuringTheSeamAttempt_ReturnsNoCut()
    {
        var payload = string.Concat(Enumerable.Repeat("x ", 40_000));
        var control = new RedactionPipeline.ScanBudget(new StepClock(int.MaxValue));
        RedactionPipeline.FindCut(payload, 0, RedactionPipeline.MaxWindowChars, payload.Length, whitespaceOnly: true, control)
            .ShouldBe(RedactionPipeline.MaxWindowChars);
        control.Aborted.ShouldBeFalse();

        // Reads: the budget's start, the check before the attempt, then the check after it.
        var spent = new RedactionPipeline.ScanBudget(new StepClock(2));

        RedactionPipeline.FindCut(payload, 0, RedactionPipeline.MaxWindowChars, payload.Length, whitespaceOnly: true, spent)
            .ShouldBe(-1);

        spent.Aborted.ShouldBeTrue();
    }

    private static string WorstCase(string shape)
    {
        var unit = shape switch
        {
            "near-miss prefixes" => ".fill( passwore=\"\n",
            "unterminated quoted selectors" => ".fill(a\\'\n",
            "unclosed brackets" => ".fill(a(b(c(d(e(f(",
            "base64-looking runs" => new string('a', 1023) + "!",
            _ => Key + "=" + new string(' ', 65_000) + "x",
        };
        var repeated = string.Concat(Enumerable.Repeat(unit, (65536 / unit.Length) + 1));
        return repeated[..65536];
    }

    private static bool ContainsBytes(string text, string value) =>
        Encoding.UTF8.GetBytes(text).AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Fullwidth(string ascii) =>
        string.Concat(ascii.Select(value => value == ' ' ? '\u3000' : (char)(value + 0xFEE0)));

    private static string Circled(string lower) =>
        string.Concat(lower.Select(value => (char)(0x24D0 + (value - 'a'))));

    private static string Mathematical(string lower) =>
        string.Concat(lower.Select(value => char.ConvertFromUtf32(0x1D41A + (value - 'a'))));

    private static string PemHeader(string edge) => string.Concat("-----", edge, " PRIVATE", " KEY-----");

    // Reads past allowedReads report a spent budget; time itself never moves, so the test cannot flake on a slow host.
    private sealed class StepClock(int allowedReads) : TimeProvider
    {
        private int _reads;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            Interlocked.Increment(ref _reads) > allowedReads ? RedactionPipeline.Budget.Ticks + 1 : 0;
    }
}
