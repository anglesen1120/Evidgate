using System.Diagnostics;
using Evidgate.BuildingBlocks.Observability.Redaction;
using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

public sealed partial class RedactionLeakCorpusTests
{
    public static TheoryData<string, string, string, string> RuleLeaks => new()
    {
        { "authorization JSON comma tail", "{\"authorization\": ***," + Secret + "}", Secret, "authorization_header" },
        { "authorization object closing-brace tail", "{'authorization': ***}" + Secret, Secret, "authorization_header" },
        { "authorization JSON private tail", "{\"authorization\": *** " + Secret + "}", Secret, "authorization_header" },
        { "fill concatenated value", "page.fill('#pw','hun'+'ter2')", "ter2", "input_value" },
        { "fill marker concatenated tail", "page.fill(sel,'[redacted:input_value]'+'" + Secret + "')", Secret, "input_value" },
        { "fill wrapped selector", "page.fill(\n SELECTORS\n .password,\n '" + Secret + "',\n)", Secret, "input_value" },
        { "fill identifier options", "locator.fill('" + Secret + "', opts)", Secret, "input_value" },
        { "python press_sequentially", "locator.press_sequentially('" + Secret + "')", Secret, "input_value" },
        { "JSON multiline LF value", "{\"value\":\"first\n" + Secret + "\"}", Secret, "input_value" },
        { "JSON multiline CRLF value", "{\"value\":\"first\r\n" + Secret + "\"}", Secret, "input_value" },
        { "JSON multiline truncated value", "{\"value\":\"first\n" + Secret, Secret, "input_value" },
        { "JSON multiline escaped quote", "{\"value\":\"first\\\"\n" + Secret + "\"}", Secret, "input_value" },
        { "object concatenated value", "{value:'hun'+'" + Secret + "'}", Secret, "input_value" },
        { "JSON concatenated value", "{\"value\":\"hun\"+\"" + Secret + "\"}", Secret, "input_value" },
        { "object marker concatenated value", "{value:'[redacted:input_value]'+'" + Secret + "'}", Secret, "input_value" },
        { "object template literal value", "{ value: `" + Secret + "` }", Secret, "input_value" },
        { "fill truncated literal", "x.fill('" + Secret, Secret, "input_value" },
        { "fill three-deep nested tail", "x.fill('" + Secret + "' + a(b(c(d))))", Secret, "input_value" },
        { "C# FillAsync", "locator.FillAsync(\"" + Secret + "\")", Secret, "input_value" },
        { "C# TypeAsync", "page.TypeAsync(SEL.a, \"" + Secret + "\")", Secret, "input_value" },
        { "C# PressSequentiallyAsync", "locator.PressSequentiallyAsync(\"" + Secret + "\")", Secret, "input_value" },
        { "Selenium sendKeys", "element.sendKeys('" + Secret + "')", Secret, "input_value" },
        { "Selenium send_keys", "element.send_keys('" + Secret + "')", Secret, "input_value" },
        { "WebdriverIO setValue", "element.setValue('" + Secret + "')", Secret, "input_value" },
        { "object value", "{ value: '" + Secret + "' }", Secret, "input_value" },
        { "quoted object value", "{'value':'" + Secret + "'}", Secret, "input_value" },
        { "private key legacy headers", PemHeader("BEGIN") + "\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-256-CBC,1234\n" + Token + "\n" + PemHeader("END"), Token, "private_key" },
        { "private key timestamp prefixes", PemHeader("BEGIN") + "\n[12:34:56] " + Token + "\n[12:34:56] " + PemHeader("END"), Token, "private_key" },
        { "private key escaped plus and slash", PemHeader("BEGIN") + "\\n" + Token + "\\u002B\\/" + Token + "\\n" + PemHeader("END"), Token, "private_key" },
        { "private key double escaped newline", PemHeader("BEGIN") + "\\\\n" + Token + "\\\\n" + PemHeader("END"), Token, "private_key" },
        { "private key missing end", PemHeader("BEGIN") + "\nProc-Type: 4,ENCRYPTED\n" + Token, Token, "private_key" },
        { "PGP private key block", string.Concat("-----BEGIN PGP PRIVATE", " KEY BLOCK-----\n") + Token + string.Concat("\n-----END PGP PRIVATE", " KEY BLOCK-----"), Token, "private_key" },
        { "orphan private key end", Token + "\n" + PemHeader("END"), Token, "residual_private_key" },
        { "orphan PGP private key end", Token + string.Concat("\n-----END PGP PRIVATE", " KEY BLOCK-----"), Token, "residual_private_key" },
    };

    public static TheoryData<string, string, string, string> PrefixedRuleLeaks
    {
        get
        {
            var rows = new TheoryData<string, string, string, string>();
            foreach (var prefix in new[] { "f", "r", "b", "u", "fr", "rf", "br", "rb", "ur", "ru" })
            {
                foreach (var quote in new[] { "'", "\"" })
                {
                    rows.Add("python prefixed fill " + prefix + quote,
                        "locator.fill(" + prefix + quote + Secret + quote + ")", Secret, "input_value");
                    rows.Add("python prefixed press_sequentially " + prefix + quote,
                        "locator.press_sequentially(" + prefix + quote + Secret + quote + ")", Secret, "input_value");
                }
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(RuleLeaks))]
    [MemberData(nameof(PrefixedRuleLeaks))]
    public void Redact_ExtendedRuleCorpus_RemovesSecretBytesOrBlocks(string name, string payload, string secretBytes, string rule) =>
        Redact_GoldenLeakCorpus_RemovesTheSecretBytesOrBlocks(name, payload, secretBytes, rule);

    public static TheoryData<string, string, string, string> TokenLeaks => new()
    {
        { "JWT", string.Concat("eyJ", "abcdefgh.abcdefgh.abcdefgh"), "abcdefgh", "token_shape" },
        { "fine-grained GitHub token", string.Concat("github", "_pat_", Token), Token, "token_shape" },
        { "AWS access key", string.Concat("AK", "IA", "1234567890ABCDEF"), "1234567890ABCDEF", "api_key_shape" },
        { "AWS session key", string.Concat("AS", "IA", "1234567890ABCDEF"), "1234567890ABCDEF", "api_key_shape" },
        { "Slack token", string.Concat("xo", "xb-", Token), Token, "api_key_shape" },
        { "Google API key", string.Concat("AI", "za", new string('a', 35)), new string('a', 35), "api_key_shape" },
        { "international phone US", string.Concat("+1 202 ", "555 0123"), "202 555 0123", "phone" },
    };

    [Theory]
    [MemberData(nameof(TokenLeaks))]
    public void Redact_TokenFamilyCorpus_RemovesSecretBytesOrBlocks(string name, string payload, string secretBytes, string rule) =>
        Redact_GoldenLeakCorpus_RemovesTheSecretBytesOrBlocks(name, payload, secretBytes, rule);

    public static TheoryData<string> TokenNearMisses => new()
    {
        string.Concat("eyJ", "abcdefg.abcdefgh.abcdefgh"),
        string.Concat("github", "_pat_", new string('a', 19)),
        string.Concat("AK", "IA", "1234567890ABCDE"),
        string.Concat("AS", "IA", "1234567890ABCDE"),
        string.Concat("xo", "xb-", new string('a', 9)),
        string.Concat("AI", "za", new string('a', 34)),
        string.Concat("+1 ", "2345678"),
    };

    [Theory]
    [MemberData(nameof(TokenNearMisses))]
    public void Redact_TokenNearMissCorpus_KeepsEveryByte(string payload) =>
        Redact_BenignCorpus_KeepsEveryByteAndDoesNotBlock(payload);

    public static TheoryData<string> MaskedAuthorizationShapes => new()
    {
        "{\"authorization\": ***}",
        "{'authorization': ***}",
        "{\"Authorization\": *** \t}",
        "{\"authorization\": ***,\"ok\":true}",
        "{\"authorization\": ***, 'ok': true}",
        "[{\"authorization\": ***}]",
        "call({'authorization': ***})",
        "Authorization: ***",
        "{\"authorization\": ***,\n\"ok\":true}",
    };

    [Theory]
    [MemberData(nameof(MaskedAuthorizationShapes))]
    public void Redact_MaskedAuthorizationInJsonOrObjectLiteral_IsLeftAlone(string payload)
    {
        var redactor = new SecretRedactor();

        redactor.RedactText(payload).ShouldBe(payload);
        Redact_BenignCorpus_KeepsEveryByteAndDoesNotBlock(payload);
    }

    [Fact]
    public void Redact_QuotedAuthorizationWithFollowingMember_MasksOnceAndKeepsTheRest()
    {
        var payload = "{\"authorization\":\"Basic " + Token + "\",\"ok\":true}";

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldBe("{\"authorization\": ***,\"ok\":true}");
        outcome.Report.Findings.ShouldHaveSingleItem().ShouldBe(new RedactionFinding("authorization_header", "payload", 1));
        new SecretRedactor().RedactText(outcome.Text).ShouldBe(outcome.Text);
    }

    [Fact]
    public void Apply_PrettyPrintedJsonValue_KeepsTheWhitespaceAroundTheMarker()
    {
        var text = RedactionRules.Apply("{\n  \"value\": \"" + Secret + "\"\n}", new Dictionary<string, int>());

        text.ShouldBe("{\n  \"value\": \"[redacted:input_value]\"\n}");
    }

    [Theory]
    [InlineData(".fill(a\\'\n", "", false)]
    [InlineData(".fill(a\\'", "", false)]
    [InlineData("\\'", ".fill('x' + ", true)]
    public void Apply_UnterminatedQuotedSelectors_ScalesFrom16To64KiBWithoutTimeout(string repeat, string prefix, bool masked)
    {
        var large = (prefix + string.Concat(Enumerable.Repeat(repeat, 65536 / repeat.Length + 1)))[..65536];
        var small = large[..16384];
        _ = MeasureSelectors(small, masked);
        _ = MeasureSelectors(large, masked);

        var smallTicks = Enumerable.Range(0, 3).Select(_ => MeasureSelectors(small, masked)).Min();
        var largeTicks = Enumerable.Range(0, 3).Select(_ => MeasureSelectors(large, masked)).Min();

        // A linear scan grows fourfold; timer resolution and loaded hosts need headroom.
        largeTicks.ShouldBeLessThan((smallTicks * 10) + (Stopwatch.Frequency / 100));
    }

    // A literal whose argument never ends is masked up to the end of the text; the selector shapes stay unchanged.
    private static long MeasureSelectors(string payload, bool masked)
    {
        var counts = new Dictionary<string, int>();
        var start = Stopwatch.GetTimestamp();
        var text = RedactionRules.Apply(payload, counts);
        var elapsed = Stopwatch.GetTimestamp() - start;
        counts.ShouldNotContainKey("regex_timeout");
        text.ShouldBe(masked ? ".fill(\"[redacted:input_value]\"" : payload);
        return elapsed;
    }
}
