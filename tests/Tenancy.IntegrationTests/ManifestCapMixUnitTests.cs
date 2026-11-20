// Test inputs are written inline so each case reads as the document it checks.
#pragma warning disable CA1861
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The 50-detail cap when secret and unclean-text refusals share it: secret details take the slots first (planned
/// executions, then producer hints), unclean text fills what is left, each group in document order, and nothing is echoed.
/// </summary>
public sealed class ManifestCapMixUnitTests
{
    private static readonly Guid Suite = Guid.Parse("00000000-0000-0000-0000-000000000020");
    private static readonly Guid CaseRevision = Guid.Parse("00000000-0000-0000-0000-000000000010");

    /// <summary>unclean rows, secret rows, secret hint, then the kept secret, hint and unclean details.</summary>
    public static TheoryData<int, int, bool, int, int, int> Mixes() => new()
    {
        { 19, 30, false, 30, 0, 19 },
        { 20, 30, false, 30, 0, 20 },
        { 21, 30, false, 30, 0, 20 },
        { 50, 0, false, 0, 0, 50 },
        { 51, 0, false, 0, 0, 50 },
        { 0, 50, false, 50, 0, 0 },
        { 0, 51, false, 50, 0, 0 },
        { 5, 50, false, 50, 0, 0 },
        { 50, 0, true, 0, 1, 49 },
        { 1, 48, true, 48, 1, 1 },
        { 1, 49, true, 49, 1, 0 },
        { 0, 50, true, 50, 0, 0 },
    };

    public static TheoryData<int, int, bool> Inputs() => new() { { 21, 30, false }, { 51, 0, false }, { 0, 51, false }, { 50, 0, true }, { 1, 49, true } };

    private static ManifestRefusal Refuse(JsonNode manifest)
    {
        using var document = JsonDocument.Parse(manifest.ToJsonString());
        var (facts, refusal) = ManifestVerifier.Verify(document.RootElement.Clone());
        facts.ShouldBeNull();

        return refusal!;
    }

    [Theory]
    [MemberData(nameof(Mixes))]
    public void TheCap_FillsSecretsFirst_ThenUncleanText_InDocumentOrder(int unclean, int secrets, bool hint, int keptSecrets, int keptHints, int keptUnclean)
    {
        var refusal = Refuse(ManifestCapMixFixture.Build(Suite, CaseRevision, unclean, secrets, hint));

        refusal.Details.Count.ShouldBe(keptSecrets + keptHints + keptUnclean);
        refusal.Details.Count.ShouldBeLessThanOrEqualTo(ManifestVerifier.MaxDetails);

        var expected = new List<(string Path, string Reason, int? Index)>();
        for (var row = unclean; row < unclean + keptSecrets; row++)
        {
            expected.Add(($"/planned_executions/{row}/variant/params/#2", ManifestVerifier.SecretInVariantParams, row));
        }

        if (keptHints > 0)
        {
            expected.Add(("/shard_plan/shards/0/producer_hint", ManifestVerifier.SecretValueDetected, null));
        }

        for (var row = 0; row < keptUnclean; row++)
        {
            expected.Add(($"/planned_executions/{row}/variant/params/#2", ManifestVerifier.InvalidValue, row));
        }

        refusal.Details.Select(detail => (detail.Path, detail.Reason, detail.Index)).ShouldBe(expected);
        refusal.Reason.ShouldBe(expected[0].Reason);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void TheDetails_AreIdenticalOnARepeat_AndNeverEchoAValue(int unclean, int secrets, bool hint)
    {
        var first = JsonSerializer.Serialize(Refuse(ManifestCapMixFixture.Build(Suite, CaseRevision, unclean, secrets, hint)).Details);
        var second = JsonSerializer.Serialize(Refuse(ManifestCapMixFixture.Build(Suite, CaseRevision, unclean, secrets, hint)).Details);

        second.ShouldBe(first);
        foreach (var echo in ManifestCapMixFixture.Echoes)
        {
            first.ShouldNotContain(echo, Case.Insensitive);
        }
    }

    /// <summary>The same rows without the refusals are accepted, so a refusal never comes from the row count or the matrix.</summary>
    [Theory]
    [InlineData(21, 30)]
    [InlineData(5, 50)]
    public void TheCleanTwinOfALargeMix_IsAcceptedWithEveryRow(int unclean, int secrets)
    {
        using var document = JsonDocument.Parse(ManifestCapMixFixture.Build(Suite, CaseRevision, unclean, secrets, hintSecret: false, clean: true).ToJsonString());

        var (facts, refusal) = ManifestVerifier.Verify(document.RootElement.Clone());

        refusal.ShouldBeNull();
        facts!.PlannedCount.ShouldBe(unclean + secrets);
    }
}
