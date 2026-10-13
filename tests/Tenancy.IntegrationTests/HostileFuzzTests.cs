// 06.3.2-T2.1 (AC4, plan §9 "Nightly fuzz budget finds no unreported crash"): the nightly fuzz run. It is the
// same class in every stage — only its bounds change: `test` and `fixtures-hostile` run the few-second smoke
// default, and the `fixtures-fuzz` stage exports EVIDGATE_FUZZ_BUDGET_SECONDS=600 for the ten-minute budget, so
// the extra minutes never land on the merge path. The seed is always printed, so a red nightly run is replayable
// with EVIDGATE_FUZZ_SEED.
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.2-T2.1 (AC4): the seeded-mutation fuzz run over <c>IParsing.ParseXmlReport</c> and
/// <c>IParsing.EnumerateArchive</c>, seeded from every corpus manifest entry (the cheap generated object
/// included; see <see cref="HostileFuzz.LoadSeeds"/> for why the archive bomb is not one). The run passes only
/// when every mutated input answered with a documented outcome — a success, or a refusal carrying the code,
/// status and reason the profile documents. Any escape, any abandoned input and any undocumented reason fails
/// the test and leaves its bytes under the findings directory.
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class HostileFuzzTests(ITestOutputHelper output)
{
    /// <summary>
    /// AC4: the run reports zero unhandled exceptions and zero timeouts beyond the documented rejections, for its
    /// whole budget, and records the seed and the counts so a green run is still evidence.
    /// </summary>
    [Fact]
    public void TheSeededMutationRunFindsNoUndocumentedOutcome()
    {
        var options = FuzzRunOptions.FromEnvironment();

        output.WriteLine(
            $"fuzz seed {options.Seed}; budget {options.BudgetSeconds}s; cap {options.MaxIterations} iterations; "
            + $"per-input {options.PerInputMilliseconds}ms; findings {options.FindingsDirectory}");

        var result = HostileFuzz.Run(options, TestContext.Current.CancellationToken, output.WriteLine);

        output.WriteLine(
            $"fuzz run: {result.Iterations} iteration(s) over {result.SeedCount} seed(s) in "
            + $"{result.Elapsed.TotalSeconds:F1}s; {result.Findings.Count} finding(s); seed {result.Seed}");

        foreach (var finding in result.Findings)
        {
            output.WriteLine($"  finding: {finding.SeedName} [{finding.Target}] #{finding.Iteration}: {finding.Detail}");
        }

        result.IsClean.ShouldBeTrue(
            $"{result.Findings.Count} input(s) answered outside the documented outcomes; the bytes are under "
            + $"{options.FindingsDirectory}");
    }
}
