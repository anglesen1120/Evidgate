using System.Text.Json;
using Evidgate.Modules.Catalog.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>03.2.1 without a database: secret shapes (D5, AC4, AC8), variant normalisation (T2.1) and the dependency DAG (T3.2, AC3).</summary>
public sealed class CatalogSuiteUnitTests
{
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    // ------------------------------------------------------------------ SecretShape

    [Theory]
    [InlineData("api_token", true)]
    [InlineData("apiToken", true)]
    [InlineData("API-KEY", true)]
    [InlineData("Password", true)]
    [InlineData("client_secret", true)]
    [InlineData("credential", true)]
    [InlineData("credentials", true)]
    [InlineData("bearer", true)]
    [InlineData("db.password", true)]
    [InlineData("accesstoken", true)]
    [InlineData("key", true)]
    [InlineData("browser", false)]
    [InlineData("keyboard", false)]
    [InlineData("monkey", false)]
    [InlineData("viewport", false)]
    [InlineData("auth", false)]
    [InlineData("session_count", false)]
    [InlineData("locale", false)]
    public void SecretShape_KeyLooksSecret(string key, bool expected) => SecretShape.KeyLooksSecret(key).ShouldBe(expected);

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", true)]
    [InlineData("github_pat_11ABCDEFG0abcdefghijklmnop", true)]
    [InlineData("glpat-abcdefghij1234567890", true)]
    [InlineData("xoxb-1234567890-abcdefghijkl", true)]
    [InlineData("sk_live_abcdefghijklmnop", true)]
    [InlineData("AKIAIOSFODNN7EXAMPLE", true)]
    [InlineData("egt_abcdefghijkl_0123456789abcdefghijklmnopqrstuvwxyzABCDEFG", true)]
    [InlineData("Bearer abcdef", true)]
    [InlineData("Basic dXNlcjpwYXNz", true)]
    [InlineData("-----BEGIN PRIVATE KEY-----", true)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVP", true)]
    [InlineData("https://user:hunter2pass@example.com/path", true)]
    [InlineData("q8Zt3RkP0vLm9XwB2nYc5HsD7JfA4GeU", true)]
    [InlineData("chromium", false)]
    [InlineData("1.2.3", false)]
    [InlineData("https://staging.example.com", false)]
    [InlineData("https://staging.example.com/health?x=1", false)]
    [InlineData("/usr/local/bin/some/long/path/name", false)]
    [InlineData("a plain sentence with spaces in it, quite long", false)]
    [InlineData("018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e5f", false)]
    [InlineData("short", false)]
    [InlineData("sk-", false)]
    public void SecretShape_ValueLooksSecret(string value, bool expected) => SecretShape.ValueLooksSecret(value).ShouldBe(expected);

    [Fact]
    public void SecretShape_Entropy_RanksRandomAboveWords()
    {
        SecretShape.Entropy("aaaaaaaa").ShouldBe(0);
        SecretShape.Entropy("q8Zt3RkP0vLm9XwB2nYc5HsD7JfA4GeU").ShouldBeGreaterThan(SecretShape.Entropy("staging-web-eu-west-one-env"));
    }

    // ------------------------------------------------------------------ VariantNormalizer

    private static (NormalizedVariant? Variant, List<VariantProblem> Problems) Normalize(string? json)
    {
        var problems = new List<VariantProblem>();
        var variant = VariantNormalizer.Normalize(json is null ? null : Json(json), "/params", problems);

        return (variant, problems);
    }

    [Fact]
    public void Variant_KeyOrderDefaultsEnumCaseAndNonIdentifyingKeys_DoNotChangeIdentity()
    {
        var a = Normalize("""{"browser":"Chromium","retries":2,"headless":true}""").Variant!;
        var b = Normalize("""{"headless":true,"retries":2,"browser":" chromium ","note":"nightly","_internal":1,"description":"x","viewport":null}""").Variant!;

        b.Hash.ShouldBe(a.Hash);
        b.ParamsJson.ShouldBe(a.ParamsJson);
        a.ParamsJson.ShouldBe("""{"browser":"chromium","headless":true,"retries":2}""");
    }

    [Fact]
    public void Variant_AnIdentifyingChange_IsAnotherVariant()
    {
        Normalize("""{"browser":"chromium"}""").Variant!.Hash.ShouldNotBe(Normalize("""{"browser":"firefox"}""").Variant!.Hash);
        Normalize("""{"retries":1}""").Variant!.Hash.ShouldNotBe(Normalize("""{"retries":2}""").Variant!.Hash);
        Normalize("""{"locale":["en","vi"]}""").Variant!.Hash.ShouldNotBe(Normalize("""{"locale":["vi","en"]}""").Variant!.Hash);
    }

    [Fact]
    public void Variant_NoParams_AreTheEmptyVariant()
    {
        var empty = Normalize("{}").Variant!;

        Normalize(null).Variant!.Hash.ShouldBe(empty.Hash);
        Normalize("null").Variant!.Hash.ShouldBe(empty.Hash);
        empty.ParamsJson.ShouldBe("{}");
    }

    [Fact]
    public void Variant_SecretKeysAndValues_AreRefused_WithTheirPaths()
    {
        var (variant, problems) = Normalize("""{"api_token":"x","note_key":"y","mode":"ghp_abcdefghijklmnopqrstuvwxyz0123456789","list":["fine","sk_live_abcdefghijklmnop"]}""");

        variant.ShouldBeNull();
        problems.Select(problem => (problem.Path, problem.Reason)).ShouldBe(
        [
            ("/params/api_token", "secret_key"),
            ("/params/note_key", "secret_key"),
            ("/params/mode", "secret_value"),
            ("/params/list/1", "secret_value"),
        ]);
    }

    [Theory]
    [InlineData("""[1,2]""", "not_object")]
    [InlineData("""{"nested":{"a":1}}""", "not_scalar")]
    [InlineData("""{"list":[{"a":1}]}""", "not_scalar")]
    [InlineData("""{"bad key":1}""", "invalid_key")]
    [InlineData("""{"1leading":1}""", "invalid_key")]
    public void Variant_Shapes_AreRefused(string json, string reason)
    {
        var (variant, problems) = Normalize(json);

        variant.ShouldBeNull();
        problems.Select(problem => problem.Reason).ShouldContain(reason);
    }

    [Fact]
    public void Variant_Bounds_AreEnforced()
    {
        var manyKeys = "{" + string.Join(',', Enumerable.Range(0, 33).Select(i => $"\"k{i}\":1")) + "}";
        Normalize(manyKeys).Problems.Select(problem => problem.Reason).ShouldContain("too_many_keys");

        Normalize($"{{\"v\":\"{new string('x', 257)}\"}}").Problems.Single().Reason.ShouldBe("invalid_value");
        Normalize($"{{\"v\":[{string.Join(',', Enumerable.Range(0, 33))}]}}").Problems.Single().Reason.ShouldBe("too_many_items");
        Normalize("{\"v\":\"bad\\u0000value\"}").Problems.Single().Reason.ShouldBe("invalid_value");
        Normalize("{\"v\":\"bidi\\u202Evalue\"}").Problems.Single().Reason.ShouldBe("invalid_value");
    }

    // ------------------------------------------------------------------ DependencyGraph

    [Fact]
    public void Graph_ADag_IsOrdered_ParentsFirst()
    {
        var result = DependencyGraph.Analyse(4, [(0, 1), (0, 2), (1, 3), (2, 3)]);

        result.Cycle.ShouldBeEmpty();
        result.Order.Count.ShouldBe(4);
        result.Order.ToList().IndexOf(0).ShouldBeLessThan(result.Order.ToList().IndexOf(3));
    }

    [Fact]
    public void Graph_NoEdges_AndNoNodes_AreFine()
    {
        DependencyGraph.Analyse(3, []).Order.Count.ShouldBe(3);
        DependencyGraph.Analyse(0, []).Order.ShouldBeEmpty();
    }

    [Fact]
    public void Graph_ASelfEdge_IsACycleOfLengthOne() =>
        DependencyGraph.Analyse(2, [(1, 1)]).Cycle.ShouldBe([1, 1]);

    [Fact]
    public void Graph_ACycle_IsReportedAsAClosedPath()
    {
        var cycle = DependencyGraph.Analyse(3, [(0, 1), (1, 2), (2, 0)]).Cycle;

        cycle.Count.ShouldBe(4);
        cycle[0].ShouldBe(cycle[^1]);
        cycle.Take(3).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void Graph_NodesBelowAndAboveACycle_DoNotHideIt()
    {
        // 0 -> 1 -> 2 -> 1 (cycle 1,2), 2 -> 3 -> 4 (dangling tail below), 5 -> 0 (head above).
        var result = DependencyGraph.Analyse(6, [(5, 0), (0, 1), (1, 2), (2, 1), (2, 3), (3, 4)]);

        result.Cycle.Distinct().Order().ShouldBe([1, 2]);
        result.Cycle[0].ShouldBe(result.Cycle[^1]);
    }

    [Fact]
    public void Graph_ADeepChain_DoesNotOverflowTheStack()
    {
        const int n = 200_000;
        var chain = Enumerable.Range(0, n - 1).Select(i => (i, i + 1)).ToList();

        DependencyGraph.Analyse(n, chain).Order.Count.ShouldBe(n);

        chain.Add((n - 1, 0));
        DependencyGraph.Analyse(n, chain).Cycle.Count.ShouldBe(n + 1);
    }
}
