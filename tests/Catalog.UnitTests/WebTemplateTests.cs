using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

public sealed partial class WebTemplateTests
{
    private static string Root => RepositoryPaths.RepoRoot;
    private static string TemplateRoot => Path.Combine(Root, "templates/web/playwright-page/1");
    private static string Manifest => File.ReadAllText(Path.Combine(TemplateRoot, "template.json"));
    private static string Rules => JsonNode.Parse(Manifest)!["conformance_rules"]!.ToJsonString();

    [Fact]
    public void Template_BodyRef_Deterministic()
    {
        (string RelativePath, byte[] Bytes)[] files = [("b", Encoding.UTF8.GetBytes("second\n")), ("a", Encoding.UTF8.GetBytes("first\n"))];
        var hash = TemplateBodyHasher.Compute(files);
        TemplateBodyHasher.Compute(files).ShouldBe(hash);
        TemplateBodyHasher.Compute(files.Reverse()).ShouldBe(hash);
        TemplateBodyHasher.Compute(files.Select(f => (f.RelativePath, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(f.Bytes).Replace("\n", "\r\n", StringComparison.Ordinal))))).ShouldBe(hash);
        TemplateBodyHasher.Compute([("a", Encoding.UTF8.GetBytes("First\n")), files[0]]).ShouldNotBe(hash);
    }

    [Theory]
    [InlineData("/absolute")]
    [InlineData("../parent")]
    [InlineData("a/../b")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("")]
    [InlineData("C:/file")]
    public void Template_BodyRef_UnsafePath_Refused(string path) =>
        Should.Throw<ArgumentException>(() => TemplateBodyHasher.Compute([(path, new byte[] { 65 })]));

    [Fact]
    public void Template_BodyRef_InvalidText_Refused()
    {
        foreach (var bytes in new byte[][] { [65, 13, 66], [239, 187, 191, 65], [255], [192, 128] })
        {
            Should.Throw<ArgumentException>(() => TemplateBodyHasher.Compute([("a", bytes)]));
        }
        Should.Throw<ArgumentException>(() => TemplateBodyHasher.Compute([("a", new byte[] { 65 }), ("a", new byte[] { 66 })]));
    }

    [Fact]
    public void Template_BodyRef_MatchesMigration()
    {
        var files = ReadFiles(TemplateRoot);
        files.ContainsKey("template.json").ShouldBeTrue();
        var migration = File.ReadAllText(Path.Combine(Root, "migrations/0119_catalog_template_web_playwright_page.sql"));
        var hash = Regex.Match(migration, "sha256:[0-9a-f]{64}", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250)).Value;
        hash.ShouldNotBeEmpty();
        TemplateBodyHasher.Compute(files.Select(f => (f.Key, Encoding.UTF8.GetBytes(f.Value)))).ShouldBe(hash);
        var ruleStart = migration.IndexOf("$rules$", StringComparison.Ordinal) + "$rules$".Length;
        var ruleEnd = migration.IndexOf("$rules$", ruleStart, StringComparison.Ordinal);
        JsonNode.DeepEquals(JsonNode.Parse(migration[ruleStart..ruleEnd]), JsonNode.Parse(Rules)).ShouldBeTrue();
    }

    [Fact]
    public void WebTemplate_Renders_Conformant()
    {
        var rendered = Render();
        TemplateConformance.Evaluate(Rules, rendered).ShouldBeEmpty();
        var again = Render();
        rendered["tests/smoke.spec.ts"].ShouldNotBe(again["tests/smoke.spec.ts"]);
        rendered.Keys.ShouldContain("pages/HomePage.ts");
        rendered.Values.ShouldAllBe(text => !text.Contains("{{", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("testName", "bad'quote")]
    [InlineData("pageName", "bad\nname")]
    [InlineData("testName", "../escape")]
    [InlineData("pageName", "1Home")]
    [InlineData("pageName", "Home Page")]
    [InlineData("pageName", "home-page")]
    [InlineData("url", "https://example.com/'")]
    [InlineData("url", "https://example.com/../private")]
    [InlineData("rolePreference", "button'bad")]
    [InlineData("namePreference", "bad\nname")]
    public void WebTemplate_Render_UnsafeParameter_Refused(string key, string value) =>
        Should.Throw<ArgumentException>(() => Render(new Dictionary<string, string> { [key] = value }));

    [Fact]
    public void WebTemplate_SecretLiteral_FailsConformance()
    {
        var rendered = Render();
        var source = rendered["tests/smoke.spec.ts"];
        foreach (var extra in new[] { "const value = 'ghp_abcdefgh12345678';", "const value = { password: 'hunter2hunter2' };" })
        {
            rendered["tests/smoke.spec.ts"] = source + extra;
            TemplateConformance.Evaluate(Rules, rendered).ShouldContain("no-hardcoded-credentials");
        }
        rendered["tests/smoke.spec.ts"] = source + "const password = process.env.PASSWORD; const config = { password: 'secret:PASSWORD' };";
        TemplateConformance.Evaluate(Rules, rendered).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("evidgate.id", "other.id")]
    [InlineData("UUID", "not-a-uuid")]
    public void WebTemplate_MissingOrInvalidAnnotation_Fails(string old, string replacement)
    {
        var rendered = Render();
        var source = rendered["tests/smoke.spec.ts"];
        if (old == "UUID")
        {
            old = AnnotationParser.Parse(source).Id!.Value.ToString("D");
        }
        rendered["tests/smoke.spec.ts"] = source.Replace(old, replacement, StringComparison.Ordinal);
        TemplateConformance.Evaluate(Rules, rendered).ShouldContain("evidgate-id-present");
    }

    [Theory]
    [InlineData("export class HomePage", "class HomePage")]
    [InlineData("constructor(private readonly page: Page)", "constructor()")]
    [InlineData("getByRole", "locator")]
    public void WebTemplate_PageObjectShape_Fails(string old, string replacement)
    {
        var rendered = Render();
        rendered["pages/HomePage.ts"].ShouldContain(old);
        rendered["pages/HomePage.ts"] = rendered["pages/HomePage.ts"].Replace(old, replacement, StringComparison.Ordinal);
        TemplateConformance.Evaluate(Rules, rendered).ShouldContain("page-object-shape");
    }

    [Theory]
    [InlineData("[{\"id\":\"unknown\",\"kind\":\"new_kind\",\"file\":\"**/*.ts\"}]", "unknown")]
    [InlineData("{", "invalid-conformance-rules")]
    [InlineData("{}", "invalid-conformance-rules")]
    [InlineData("[]", "invalid-conformance-rules")]
    [InlineData("[{\"id\":\"broken\",\"kind\":\"requires_pattern\",\"file\":\"**/*.ts\",\"pattern\":\"(\"}]", "broken")]
    [InlineData("[{\"id\":\"broken\",\"kind\":\"requires_pattern\"}]", "broken")]
    public void WebTemplate_InvalidRules_FailClosed(string rules, string expected) =>
        TemplateConformance.Evaluate(rules, Render()).ShouldContain(expected);

    [Fact]
    public void WebTemplate_BoundedInputs_FailClosed()
    {
        TemplateConformance.Evaluate(new string(' ', 65537), Render()).ShouldContain("invalid-conformance-rules");
        var rendered = Render();
        rendered["tests/smoke.spec.ts"] = new string('a', 65537);
        TemplateConformance.Evaluate(Rules, rendered).ShouldContain("no-hardcoded-credentials");
        TemplateConformance.Evaluate(Rules, new Dictionary<string, string>()).ShouldContain("page-object-shape");
    }

    [Fact]
    public void WebTemplate_GoldenVerdicts_MatchEveryCaseAndRule()
    {
        var directory = Path.Combine(Root, "fixtures/template-conformance/web-playwright-page-1");
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "expected-rules.json")));
        var cases = expected.RootElement.EnumerateObject().ToArray();
        cases.Length.ShouldBe(6);
        var ids = JsonNode.Parse(Rules)!.AsArray().Select(r => r!["id"]!.GetValue<string>()).Distinct().ToArray();
        foreach (var item in cases)
        {
            var violations = TemplateConformance.Evaluate(Rules, ReadFiles(Path.Combine(directory, item.Name)));
            item.Value.EnumerateObject().Select(p => p.Name).Order().ShouldBe(ids.Order());
            foreach (var rule in item.Value.EnumerateObject())
            {
                (!violations.Contains(rule.Name)).ShouldBe(rule.Value.GetBoolean(), $"{item.Name}: {rule.Name}");
            }
        }
    }

    private static Dictionary<string, string> Render(IReadOnlyDictionary<string, string>? parameters = null) =>
        TemplateRenderer.Render(Manifest, ReadFiles(Path.Combine(TemplateRoot, "files")), parameters);

    private static Dictionary<string, string> ReadFiles(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Template input cannot contain symlinks.");
            }
            if (Directory.Exists(entry))
            {
                foreach (var file in ReadFiles(entry))
                {
                    result.Add(Path.GetFileName(entry) + "/" + file.Key, file.Value);
                }
            }
            else
            {
                result.Add(Path.GetFileName(entry), new UTF8Encoding(false, true).GetString(File.ReadAllBytes(entry)));
            }
        }
        return result;
    }
}
