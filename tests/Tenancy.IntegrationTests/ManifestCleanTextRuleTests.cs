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

    /// <summary>Identifying keys: the manifest check and the normaliser agree on clean/not-clean values.</summary>
    public static TheoryData<string, string, bool> ValueCorpus() => new()
    {
        { "mode", "\uFEFFGHP_AAAAAAAA", false },
        { "mode", "gh\u200Bp_AAAAAAAAAAAA", false },
        { "mode", "gh\u00ADp_AAAAAAAAAAAA", false },
        { "mode", "gh\u2060p_AAAAAAAAAAAA", false },
        { "mode", "gh\u202Ep_AAAAAAAAAAAA", false },
        { "mode", "gh\u0001p_AAAAAAAAAAAA", false },
        { "mode", "GHP_AAAAAAAA\uFEFF", false },
        { "region", "gh\u200Bp_AAAAAAAAAAAA", false },
        { "region", "\uFEFFvalue", false },
        { "region", "a\u007Fb", false },
        { "region", "a\u0085b", false },
        { "region", "a\u200Db", false },
        { "region", "a\u200Cb", false },
        { "region", "\uD83D\uDC68\u200D\uD83D\uDC69", false },
        { "region", "plain", true },
        { "region", "ünïcödé 日本語 العربية", true },
        { "region", "emoji \uD83D\uDE00", true },
        { "region", "", true },
        { "mode", "CHROMIUM", true },
        { "region", "tab free text 123", true },
        { "api_token", "secret:ci_token", true },
    };

    /// <summary>Names never reach the verifier over HTTP (the manifest schema pins ^[a-z][a-z0-9_]{0,63}$): unit level only.</summary>
    public static TheoryData<string, bool> NameCorpus() => new()
    {
        { "la\u200Bbel", false },
        { "la\u202Ebel", false },
        { "\uFEFFmode", false },
        { "region", true },
        { "label", true },
    };

    [Theory]
    [MemberData(nameof(ValueCorpus))]
    public void IdentifyingKeys_ManifestCheck_AndNormaliser_AgreeOnCleanValues(string key, string value, bool clean)
    {
        var parameters = Params(key, value);
        var problems = new List<VariantProblem>();
        VariantNormalizer.Normalize(parameters, "/params", problems);

        (ManifestVerifier.UncleanPositions(parameters).Length == 0).ShouldBe(clean);
        problems.Any(problem => problem.Reason == "invalid_value").ShouldBe(!clean);
    }

    [Theory]
    [MemberData(nameof(NameCorpus))]
    public void Names_ManifestCheck_AndNormaliser_AgreeOnCleanNames(string key, bool clean)
    {
        var parameters = Params(key, "value");
        var problems = new List<VariantProblem>();
        VariantNormalizer.Normalize(parameters, "/params", problems);

        (ManifestVerifier.UncleanPositions(parameters).Length == 0).ShouldBe(clean);
        problems.Any(problem => problem.Reason == "invalid_key").ShouldBe(!clean);
    }

    /// <summary>Free-text keys: unclean text is accepted (ZWJ emoji, ZWNJ script), as the normaliser does; a hidden token is a secret.</summary>
    [Theory]
    [InlineData("label", "\uFEFFGHP_AAAAAAAA", true)]
    [InlineData("label", "gh\u200Bp_AAAAAAAAAAAA", true)]
    [InlineData("title", "gh\u00ADp_AAAAAAAAAAAA", true)]
    [InlineData("description", "gh\u202Ep_AAAAAAAAAAAA", true)]
    [InlineData("note", "GHP_AAAAAAAA\uFEFF", true)]
    [InlineData("description", "family \uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67", false)]
    [InlineData("title", "می\u200Cخواهم", false)]
    [InlineData("label", "क्‍ष", false)]
    [InlineData("label", "plain note", false)]
    public void FreeTextKeys_AreNeverUnclean_ButAHiddenSecretIsRefused(string key, string value, bool secret)
    {
        var parameters = Params(key, value);
        var problems = new List<VariantProblem>();
        VariantNormalizer.Normalize(parameters, "/params", problems);

        problems.ShouldBeEmpty();
        ManifestVerifier.UncleanPositions(parameters).ShouldBeEmpty();
        ManifestVerifier.SecretPositions(parameters).ShouldBe(secret ? [2] : []);
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
