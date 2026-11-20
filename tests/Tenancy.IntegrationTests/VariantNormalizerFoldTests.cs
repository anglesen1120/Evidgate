using System.Text.Json;
using Evidgate.Modules.Catalog.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The stored form of a variant value must not look like a secret, and a member name the JSON reader cannot read is a
/// refused key. Pure: <see cref="VariantNormalizer"/> touches no database. The shared corpus
/// <c>fixtures/variant-hash/vectors.json</c> carries the same cases for the TypeScript port.
/// </summary>
public sealed class VariantNormalizerFoldTests
{
    private static (NormalizedVariant? Variant, List<VariantProblem> Problems) Normalize(string json)
    {
        var problems = new List<VariantProblem>();
        var variant = VariantNormalizer.Normalize(JsonSerializer.Deserialize<JsonElement>(json), "/params", problems);

        return (variant, problems);
    }

    /// <summary>An upper-cased token prefix under an enum-like key is secret-shaped once folded: refused, not stored lower-cased.</summary>
    [Theory]
    [InlineData("GHP_AAAAAAAA")]
    [InlineData("GITHUB_PAT_AAAAAAAA")]
    [InlineData("XOXB-AAAAAAAA")]
    [InlineData("  GHP_AAAAAAAA  ")]
    [InlineData("SK-LIVE-CHANNEL")]
    [InlineData("\u00a0GHP_AAAAAAAA\u00a0")]
    [InlineData("S\u212a-AAAAAAAA")]
    public void EnumLikeValue_WhoseFoldedFormIsSecretShaped_IsRefused(string value)
    {
        var (variant, problems) = Normalize(JsonSerializer.Serialize(new { mode = value }));

        variant.ShouldBeNull();
        problems.ShouldHaveSingleItem().ShouldBe(new VariantProblem("/params/mode", "secret_value"));
    }

    /// <summary>The same rule reaches array items, which fold under their owning key.</summary>
    [Fact]
    public void EnumLikeArrayItem_WhoseFoldedFormIsSecretShaped_IsRefusedAtItsIndex()
    {
        var (variant, problems) = Normalize("""{"locale":["en","GHP_AAAAAAAA"]}""");

        variant.ShouldBeNull();
        problems.ShouldHaveSingleItem().ShouldBe(new VariantProblem("/params/locale/1", "secret_value"));
    }

    /// <summary>Values that fold to something ordinary, and values under keys that do not fold, keep their old answer.</summary>
    [Theory]
    [InlineData("""{"mode":"GHP_MODE"}""", """{"mode":"ghp_mode"}""")]
    [InlineData("""{"browser":"CHROMIUM"}""", """{"browser":"chromium"}""")]
    [InlineData("""{"other":"GHP_AAAAAAAA"}""", """{"other":"GHP_AAAAAAAA"}""")]
    [InlineData("""{"mode":"ghost_town_mode"}""", """{"mode":"ghost_town_mode"}""")]
    public void OrdinaryValues_KeepTheirStoredForm(string input, string stored)
    {
        var (variant, problems) = Normalize(input);

        problems.ShouldBeEmpty();
        variant.ShouldNotBeNull().ParamsJson.ShouldBe(stored);
    }

    /// <summary>A lone-surrogate key is an <c>invalid_key</c> by position (never a thrown reader error), and nothing is echoed.</summary>
    [Fact]
    public void LoneSurrogateKey_IsRefusedByPosition_WithoutThrowing()
    {
        var (variant, problems) = Normalize("""{"retries":1,"\ud800":1,"x\udc00y":2}""");

        variant.ShouldBeNull();
        problems.ShouldBe([new VariantProblem("/params/#2", "invalid_key"), new VariantProblem("/params/#3", "invalid_key")]);
        problems.ShouldAllBe(problem => !problem.Path.Any(char.IsSurrogate));
    }

    /// <summary>No refusal names the value or the key that caused it.</summary>
    [Fact]
    public void Refusals_NeverEchoTheValue()
    {
        var (variant, problems) = Normalize("""{"mode":"GHP_ZZECHOZZ"}""");

        variant.ShouldBeNull();
        problems.ShouldHaveSingleItem().Reason.ShouldBe("secret_value");
        problems.ShouldAllBe(problem => !problem.Path.Contains("ECHO", StringComparison.OrdinalIgnoreCase) && !problem.Reason.Contains("ECHO", StringComparison.OrdinalIgnoreCase));
    }
}
