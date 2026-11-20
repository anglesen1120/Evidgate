using System.Text.Json;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The manifest check and the variant normaliser share one text-cleanliness rule: a parameter name or string value with a
/// control, format or invisible character is refused by position here exactly where the normaliser refuses it. Pure.
/// </summary>
public sealed class ManifestCleanTextRuleTests
{
    private static JsonElement Params(string key, string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["retries"] = "1", [key] = value }));

        return document.RootElement.Clone();
    }

    // Names with invisible characters never reach the verifier over HTTP (the manifest schema pins
    // ^[a-z][a-z0-9_]{0,63}$), so the name rows are unit level only.
    public static TheoryData<string, string, bool> Corpus() => new()
    {
        { "mode", "\uFEFFGHP_AAAAAAAA", false },
        { "mode", "gh\u200Bp_AAAAAAAAAAAA", false },
        { "mode", "gh\u00ADp_AAAAAAAAAAAA", false },
        { "mode", "gh\u2060p_AAAAAAAAAAAA", false },
        { "mode", "gh\u202Ep_AAAAAAAAAAAA", false },
        { "mode", "gh\u0001p_AAAAAAAAAAAA", false },
        { "mode", "GHP_AAAAAAAA\uFEFF", false },
        { "label", "gh\u200Bp_AAAAAAAAAAAA", false },
        { "label", "\uFEFFvalue", false },
        { "label", "a\u007Fb", false },
        { "label", "a\u0085b", false },
        { "label", "a\u200Db", false },
        { "label", "a\u200Cb", false },
        { "label", "\uD83D\uDC68\u200D\uD83D\uDC69", false },
        { "la\u200Bbel", "value", false },
        { "la\u202Ebel", "value", false },
        { "label", "plain", true },
        { "label", "ünïcödé 日本語 العربية", true },
        { "label", "emoji \uD83D\uDE00", true },
        { "label", "", true },
        { "mode", "CHROMIUM", true },
        { "label", "tab free text 123", true },
        { "api_token", "secret:ci_token", true },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void ManifestCheck_AndNormaliser_AgreeOnCleanText(string key, string value, bool clean)
    {
        var parameters = Params(key, value);
        var problems = new List<VariantProblem>();
        VariantNormalizer.Normalize(parameters, "/params", problems);

        (ManifestVerifier.UncleanPositions(parameters).Length == 0).ShouldBe(clean);
        problems.Any(problem => problem.Reason == "invalid_value").ShouldBe(!clean);
    }

    [Theory]
    [InlineData("mode", "\uFEFFGHP_AAAAAAAA", 2)]
    [InlineData("label", "gh\u200Bp_AAAAAAAAAAAA", 2)]
    [InlineData("mode", "GHP_AAAAAAAA\uFEFF", 2)]
    public void AnUncleanValue_IsRefusedAtItsPosition(string key, string value, int position) =>
        ManifestVerifier.UncleanPositions(Params(key, value)).ShouldBe([position]);

    [Fact]
    public void ACleanSecret_StaysASecretPosition_NotAnUncleanOne()
    {
        var parameters = Params("mode", "GHP_AAAAAAAA");

        ManifestVerifier.UncleanPositions(parameters).ShouldBeEmpty();
        ManifestVerifier.SecretPositions(parameters).ShouldBe([2]);
    }
}
