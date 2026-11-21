using System.Diagnostics;
using Evidgate.Modules.Catalog.Domain;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

public sealed partial class WebTemplateTests
{
    private const string SecretRules = """[{"id":"secret","kind":"forbids_secret_literal","file":"**/*.ts"}]""";

    [Theory]
    [InlineData("const password = `hunter2hunter2`;", "const password = process.env.PASSWORD;")]
    [InlineData("const config = {token: `x`};", "const config = {token: `secret:TOKEN`};")]
    [InlineData("const config = {'password': 'x'};", "const config = {'password': 'secret:PASSWORD'};")]
    [InlineData("const config = {\"password\": 'x'};", "const config = {\"password\": 'secret:PASSWORD'};")]
    [InlineData("const password: string = 'x';", "const password: string = 'secret:PASSWORD';")]
    [InlineData("const password = process.env.PASSWORD ?? 'x';", "const password = process.env.PASSWORD ?? '';")]
    [InlineData("const password = process.env.PASSWORD || 'x';", "const password = process.env.PASSWORD || 'secret:PASSWORD';")]
    [InlineData("const config = {password: process.env.PASSWORD ?? 'x'};", "const config = {password: process.env.PASSWORD ?? ''};")]
    [InlineData("const value = 'secret:ghp_abcdefghijklmnopqrstuvwx';", "const value = 'secret:PASSWORD';")]
    public void WebTemplate_SecretAssociation_RejectsLiteralAndAcceptsReference(string denied, string allowed)
    {
        TemplateConformance.Evaluate(SecretRules, new Dictionary<string, string> { ["test.ts"] = denied }).ShouldBe(["secret"]);
        TemplateConformance.Evaluate(SecretRules, new Dictionary<string, string> { ["test.ts"] = allowed }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/\\/\\//; const v='ghp_abcdefghijklmnopqrstuvwx';")]
    [InlineData("/'/; const v='ghp_abcdefghijklmnopqrstuvwx'; /'/")]
    [InlineData("const value = /'/;")]
    public void WebTemplate_PossibleRegex_FailsClosed(string source)
    {
        TemplateConformance.Evaluate(SecretRules, new Dictionary<string, string> { ["test.ts"] = source }).ShouldBe(["secret"]);
        TemplateConformance.Evaluate(SecretRules, new Dictionary<string, string> { ["test.ts"] = "const v = a / b; const name = 'hello';" }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void WebTemplate_JavaScriptLineTerminators_DoNotHideSecrets(string terminator)
    {
        var token = "ghp_abcdefghijklmnopqrstuvwx";
        var continued = "const v = 'ghp_abcdefgh\\" + terminator + "ijklmnopqrstuvwx';";
        TemplateConformance.Evaluate(SecretRules, new Dictionary<string, string> { ["test.ts"] = continued }).ShouldBe(["secret"]);
        var comment = "// harmless" + terminator + "const v = '" + token + "';";
        TemplateConformance.Evaluate(SecretRules, new Dictionary<string, string> { ["test.ts"] = comment }).ShouldBe(["secret"]);
        TemplateConformance.Evaluate(SecretRules, new Dictionary<string, string> { ["test.ts"] = "// '" + token + "'" }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(127)]
    [InlineData(0xD800)]
    public void Template_BodyRef_ControlOrMalformedPath_Refused(int code)
    {
        var path = "files/" + (char)code + ".ts";
        Should.Throw<ArgumentException>(() => TemplateBodyHasher.Compute([(path, new byte[] { 65 })])).GetType().ShouldBe(typeof(ArgumentException));
        TemplateBodyHasher.Compute([("files/ok.ts", new byte[] { 65 })]).ShouldStartWith("sha256:");
    }

    [Fact]
    public void Template_BodyRef_FileAndByteLimits_Enforced()
    {
        var files = Enumerable.Range(0, 256).Select(index => ($"file{index}", new byte[] { 65 })).ToArray();
        TemplateBodyHasher.Compute(files).ShouldStartWith("sha256:");
        Should.Throw<ArgumentException>(() => TemplateBodyHasher.Compute(files.Append(("overflow", new byte[] { 65 }))));
        TemplateBodyHasher.Compute([("a", new byte[8 * 1024 * 1024])]).ShouldStartWith("sha256:");
        Should.Throw<ArgumentException>(() => TemplateBodyHasher.Compute([("a", new byte[8 * 1024 * 1024]), ("b", new byte[] { 65 })]));
    }

    [Theory]
    [InlineData("https://example.com/\u0001")]
    [InlineData("https://example.com/\u007f")]
    [InlineData("https://example.com/é")]
    [InlineData("https://user" + ":x@example.com")]
    [InlineData("https://example.com/a b")]
    public void WebTemplate_Render_UnsafeUrl_Refused(string url)
    {
        Should.Throw<ArgumentException>(() => Render(new Dictionary<string, string> { ["url"] = url }));
        Render(new Dictionary<string, string> { ["url"] = "https://example.com/path?q=1" }).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("../escape.ts.tmpl")]
    [InlineData("/absolute.ts.tmpl")]
    [InlineData("a\u0001.ts.tmpl")]
    [InlineData("a\\b.ts.tmpl")]
    public void WebTemplate_Render_UnsafeTarget_Refused(string path)
    {
        Should.Throw<ArgumentException>(() => TemplateRenderer.Render(Manifest, new Dictionary<string, string> { [path] = "text" }));
        TemplateRenderer.Render(Manifest, new Dictionary<string, string> { ["a.ts.tmpl"] = "text" }).Keys.ShouldBe(["a.ts"]);
    }

    [Fact]
    public void WebTemplate_Render_ContentCap_EnforcedBeforeSubstitution()
    {
        TemplateRenderer.Render(Manifest, new Dictionary<string, string> { ["a.ts"] = new('a', 65536) }).ShouldNotBeEmpty();
        Should.Throw<ArgumentException>(() => TemplateRenderer.Render(Manifest, new Dictionary<string, string> { ["a.ts"] = new('a', 65537) }));
        Should.Throw<ArgumentException>(() => TemplateRenderer.Render(Manifest, new Dictionary<string, string> { ["a.ts"] = new string('a', 65536) + "{{unknown}}" }))
            .Message.ShouldContain("too large");
    }

    [Fact]
    public void WebTemplate_RuleAndPatternBudgets_FailClosed()
    {
        const string Rule = """{"id":"bounded","kind":"requires_pattern","file":"**/*.ts","pattern":["."]}""";
        const string EightPatterns = """{"id":"bounded","kind":"requires_pattern","file":"**/*.ts","pattern":[".",".",".",".",".",".",".","."]}""";
        var files = new Dictionary<string, string> { ["test.ts"] = "ok" };
        TemplateConformance.Evaluate(Document(Rule, 32), files).ShouldBeEmpty();
        TemplateConformance.Evaluate(Document(Rule, 33), files).ShouldBe(["invalid-conformance-rules"]);
        TemplateConformance.Evaluate(Document(EightPatterns, 8), files).ShouldBeEmpty();
        TemplateConformance.Evaluate(Document(EightPatterns, 9), files).ShouldBe(["invalid-conformance-rules"]);
    }

    [Theory]
    [InlineData("unknown", ".")]
    [InlineData("requires_pattern", "(")]
    public void WebTemplate_InvalidKindOrPattern_MatchingGlobFails(string kind, string pattern)
    {
        var denied = $$"""[{"id":"rule","kind":"{{kind}}","file":"**/*.ts","pattern":"{{pattern}}"}]""";
        TemplateConformance.Evaluate(denied, Render()).ShouldBe(["rule"]);
        const string Allowed = """[{"id":"rule","kind":"requires_pattern","file":"**/*.ts","pattern":"."}]""";
        TemplateConformance.Evaluate(Allowed, Render()).ShouldBeEmpty();
    }

    private static string Document(string rule, int count) =>
        "[" + string.Join(",", Enumerable.Repeat(rule, count)) + "]";

    [Fact]
    public void WebTemplate_NonDefaultRoleAndName_RenderConformant()
    {
        var rendered = Render(new Dictionary<string, string> { ["rolePreference"] = "link", ["namePreference"] = "Continue" });
        TemplateConformance.Evaluate(Rules, rendered).ShouldBeEmpty();
        rendered["pages/HomePage.ts"].ShouldContain("getByRole('link', { name: 'Continue' })");
        Should.Throw<ArgumentException>(() => Render(new Dictionary<string, string> { ["rolePreference"] = "madeup" }));
    }

    [Fact]
    public void WebTemplate_Annotation_IsStaticTestDetailRecognisedByParser()
    {
        var source = Render()["tests/smoke.spec.ts"];
        source.ShouldContain("test('smoke', { annotation: { type: 'evidgate.id', description: '");
        source.ShouldNotContain("annotations.push");
        AnnotationParser.Parse(source).Outcome.ShouldBe(AnnotationOutcome.Valid);
    }

    [Theory]
    [InlineData("`")]
    [InlineData("${")]
    [InlineData("''")]
    public void WebTemplate_AdversarialInputAtCap_IsBounded(string fragment)
    {
        var source = string.Concat(Enumerable.Repeat(fragment, 65536 / fragment.Length));
        var timer = Stopwatch.StartNew();
        var scan = TemplateLiteralScanner.Scan(source);
        TemplateConformance.Evaluate(SecretRules, new Dictionary<string, string> { ["test.ts"] = source })
            .ShouldBe(scan.Complete ? [] : ["secret"]);
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}
