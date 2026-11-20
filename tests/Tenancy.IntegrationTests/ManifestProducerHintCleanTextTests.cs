using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A shard <c>producer_hint</c> is stored verbatim, so text that hides a token behind control or format characters is
/// refused by shard path: as a secret when the visible text is secret-shaped, otherwise as invalid text. Pure.
/// </summary>
public sealed class ManifestProducerHintCleanTextTests
{
    private const string Token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    private static (ManifestFacts? Facts, ManifestRefusal? Refusal) Verify(JsonNode manifest)
    {
        using var document = JsonDocument.Parse(manifest.ToJsonString());

        return ManifestVerifier.Verify(document.RootElement);
    }

    private static JsonObject WithHints(params string?[] hints)
    {
        var manifest = ManifestUnitTests.Build(Guid.NewGuid(), [Guid.NewGuid()], [new() { ["browser"] = "x" }], ["staging"]);
        var shards = new JsonArray();
        for (var i = 0; i < hints.Length; i++)
        {
            var shard = new JsonObject { ["shard_id"] = $"s{i}", ["from_index"] = 0, ["to_index"] = 1 };
            if (hints[i] is { } hint)
            {
                shard["producer_hint"] = hint;
            }

            shards.Add(shard);
        }

        manifest["shard_plan"] = new JsonObject { ["shards"] = shards };

        return manifest;
    }

    [Theory]
    [InlineData("ghp_abcdefghijkl\u200Bmnopqrstuvwxyz0123456789", "secret_value_detected")]
    [InlineData("\uFEFFghp_abcdefghijklmnopqrstuvwxyz0123456789", "secret_value_detected")]
    [InlineData("\u202Eghp_abcdefghijklmnopqrstuvwxyz0123456789", "secret_value_detected")]
    [InlineData("ghp_abcdefghijkl\u0001mnopqrstuvwxyz0123456789", "secret_value_detected")]
    [InlineData("gh\u200Bp_abcdefghijklmnopqrstuvwxyz0123456789", "secret_value_detected")]
    [InlineData("\uFEFFgh\u200Bp_abcdefghijklmnopqrstuvwxyz0123456789", "secret_value_detected")]
    [InlineData(Token, "secret_value_detected")]
    [InlineData("\uFEFFpytest", "invalid_value")]
    [InlineData("py\u200Btest", "invalid_value")]
    [InlineData("family \uD83D\uDC68\u200D\uD83D\uDC69", "invalid_value")]
    [InlineData("py\u0001test", "invalid_value")]
    public void UncleanOrSecretHint_IsRefusedByPath_NeverEchoed(string hint, string reason)
    {
        var refusal = Verify(WithHints(hint)).Refusal!;

        refusal.Reason.ShouldBe(reason);
        refusal.Details.Select(detail => detail.Path).ShouldBe(["/shard_plan/shards/0/producer_hint"]);
        refusal.Details.Select(detail => detail.Reason).ShouldBe([reason]);
        JsonSerializer.Serialize(refusal).ShouldNotContain("abcdefghijkl");
    }

    [Theory]
    [InlineData("pytest-junitxml")]
    [InlineData("ünïcödé 日本語")]
    [InlineData("emoji \uD83D\uDE00")]
    public void CleanHint_PassesTheVerifier(string hint) => Verify(WithHints(hint)).Refusal.ShouldBeNull();

    [Fact]
    public void AbsentHint_PassesTheVerifier() => Verify(WithHints((string?)null)).Refusal.ShouldBeNull();

    [Fact]
    public void MixedSecretAndUncleanHints_KeepSecretFirst_AndTheDetailCap()
    {
        var hints = Enumerable.Range(0, 49).Select(_ => (string?)Token)
            .Concat(Enumerable.Range(0, 5).Select(_ => (string?)"\uFEFFpytest")).ToArray();

        var refusal = Verify(WithHints(hints)).Refusal!;

        refusal.Details.Count.ShouldBe(ManifestVerifier.MaxDetails);
        refusal.Reason.ShouldBe("secret_value_detected");
        refusal.Details.Take(49).Select(detail => detail.Reason).Distinct().ShouldBe(["secret_value_detected"]);
        refusal.Details[49].Reason.ShouldBe("invalid_value");
    }

    [Fact]
    public void SecretParam_UncleanParam_AndBadHint_ListSecretFirst()
    {
        var manifest = ManifestUnitTests.Build(Guid.NewGuid(), [Guid.NewGuid()], [new() { ["browser"] = "x", ["region"] = "a\u200Bb", ["api_token"] = "plain" }], ["staging"]);
        manifest["shard_plan"]!["shards"]![0]!["producer_hint"] = "\uFEFFpytest";

        var refusal = Verify(manifest).Refusal!;

        refusal.Details[0].Reason.ShouldBe("secret_in_variant_params");
        refusal.Details.Where(detail => detail.Path == "/shard_plan/shards/0/producer_hint").Select(detail => detail.Reason).ShouldBe(["invalid_value"]);
        refusal.Details[^1].Reason.ShouldBe("invalid_value");
    }
}
