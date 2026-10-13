// 06.3.2-T1.3 (AC3): the corpus must be load-bearing, not decorative. The smoke proves it by running the corpus
// against ONE deliberately weakened cap and showing that a case which the production profile refuses is then
// ACCEPTED, and that restoring the cap restores the refusal. The weaker variant is a test-constructed
// ZipArchiveOptions value passed to the profile's internal Enumerate overload — the seam 06.3.1-T1 documents for
// exactly this ("so a test can prove every guard with tiny generated archives") — so no shipped path is weakened
// and no production member carries a test-only branch: ParsingService.EnumerateArchive always runs
// ZipArchiveOptions.Default, which is what the first assertion below re-establishes.
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.2-T1.3 (AC3, plan §9): the entry-count guard is load-bearing. The archive bomb is refused by the
/// production profile from its declared entry count; with <c>MaxEntries</c> raised above that count — every other
/// cap left at its production value — the very same bytes walk through; restoring the cap refuses them again.
/// Every other archive row of the corpus stays refused under the weakened options, so the mutation isolates the
/// one cap it weakened.
/// </summary>
[Trait("Category", "Hostile")]
public sealed class HostileFixtureMutationSmokeTests(GeneratedHostileFixtures generated)
    : IClassFixture<GeneratedHostileFixtures>
{
    /// <summary>
    /// The one weakened cap: the production options with <c>MaxEntries</c> above the bomb's entry count, so the
    /// bomb's count no longer trips the guard. Built with <c>with</c>, so every other cap is byte-for-byte the
    /// production value.
    /// </summary>
    private static readonly ZipArchiveOptions WeakenedEntryCountCap =
        ZipArchiveOptions.Default with { MaxEntries = GenerateBombFixture.EntryCount + 1 };

    /// <summary>
    /// AC3: the bomb is refused with the real cap (<c>entry_count_exceeded</c>, decided from the declared count
    /// before an entry is read), accepted once <c>MaxEntries</c> is raised above its count, and refused again when
    /// the production options are restored. That is what makes the corpus a gate: the same bytes change verdict
    /// only because one cap changed.
    /// </summary>
    [Fact]
    public void WeakeningTheEntryCountCapLetsTheBombThrough()
    {
        var bomb = File.ReadAllBytes(generated.PathOf(GenerateBombFixture.FileName));

        // 1. The production path (IParsing) refuses it — the cap is not weakened anywhere it ships.
        using (var production = new MemoryStream(bomb, writable: false))
        {
            var refused = new ParsingService()
                .EnumerateArchive(production, HostileFixtureCorpus.MaxBytes, TestContext.Current.CancellationToken);

            refused.IsSuccess.ShouldBeFalse("the production entry-count cap refuses the bomb");
            refused.Error!.Details!.Single().Reason.ShouldBe(ParsingFailureReason.EntryCountExceeded);
        }

        // 2. Weakened: the same bytes walk through, which is the proof the guard is what refuses them.
        using (var weakened = new MemoryStream(bomb, writable: false))
        {
            var accepted = SafeZipArchive.Enumerate(
                weakened, HostileFixtureCorpus.MaxBytes, WeakenedEntryCountCap, TestContext.Current.CancellationToken);

            accepted.IsSuccess.ShouldBeTrue(
                "with MaxEntries above the bomb's count nothing else refuses it — the entry-count cap is load-bearing");
            accepted.Value.Count.ShouldBe(GenerateBombFixture.EntryCount);
        }

        // 3. Restoring the production cap restores the refusal.
        using (var restored = new MemoryStream(bomb, writable: false))
        {
            var refusedAgain = SafeZipArchive.Enumerate(
                restored, HostileFixtureCorpus.MaxBytes, ZipArchiveOptions.Default, TestContext.Current.CancellationToken);

            refusedAgain.IsSuccess.ShouldBeFalse("the production options refuse the bomb again");
            refusedAgain.Error!.Details!.Single().Reason.ShouldBe(ParsingFailureReason.EntryCountExceeded);
        }
    }

    /// <summary>
    /// The mutation is exactly one cap wide: the weakened variant differs from the production options in
    /// <c>MaxEntries</c> and in nothing else, so a failure of the smoke cannot come from a second weakened guard.
    /// </summary>
    [Fact]
    public void TheWeakenedVariantDiffersOnlyInTheEntryCountCap()
    {
        WeakenedEntryCountCap.ShouldNotBe(ZipArchiveOptions.Default);
        WeakenedEntryCountCap.MaxEntries.ShouldBeGreaterThan(GenerateBombFixture.EntryCount);
        (WeakenedEntryCountCap with { MaxEntries = ZipArchiveOptions.Default.MaxEntries })
            .ShouldBe(ZipArchiveOptions.Default);

        ZipArchiveOptions.Default.MaxEntries.ShouldBe(ParsingLimits.MaxZipEntries);
        ZipArchiveOptions.Default.MaxExpandedBytes.ShouldBe(ParsingLimits.MaxExpandedBytes);
        ZipArchiveOptions.Default.MaxCompressionRatio.ShouldBe(ParsingLimits.MaxCompressionRatio);
        ZipArchiveOptions.Default.MaxNesting.ShouldBe(ParsingLimits.MaxNesting);
        ZipArchiveOptions.Default.Timeout.ShouldBe(ParsingLimits.ParseTimeout);
    }

    /// <summary>
    /// Every other archive row that must be refused is still refused, with its own documented code, under the same
    /// weakened options: the bomb is the one corpus case the entry-count cap carries, and the mutation never
    /// accidentally accepts a traversal, an unsafe name or a malformed archive.
    /// </summary>
    [Fact]
    public void TheWeakenedCapLeavesEveryOtherArchiveRowRefused()
    {
        foreach (var entry in HostileFixtureCorpus.All.Where(row => row.IsArchiveDriven && row.ExpectsRejection))
        {
            if (string.Equals(entry.File, GenerateBombFixture.FileName, StringComparison.Ordinal))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(
                entry.Generated ? generated.PathOf(entry.File) : HostileFixtureCorpus.PathOf(entry));

            using var content = new MemoryStream(bytes, writable: false);

            var refused = SafeZipArchive.Enumerate(
                content, HostileFixtureCorpus.MaxBytes, WeakenedEntryCountCap, TestContext.Current.CancellationToken);

            refused.IsSuccess.ShouldBeFalse($"{entry.File} is still refused with the entry-count cap weakened");
            refused.Error!.Details!.Single().Reason.ShouldBe(entry.ExpectedFailureCode);
        }
    }
}
