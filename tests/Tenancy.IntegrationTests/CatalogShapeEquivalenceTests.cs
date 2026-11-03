using System.Diagnostics;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Catalog.Application;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The Catalog identifier shapes now go through <see cref="AsciiShape"/> instead of a regex with a wall-clock
/// timeout. Each old pattern stays here as the oracle and must agree with the production predicate on every input
/// but one: `$` also accepted a single trailing newline, and the helper is anchored like `\z` and refuses it.
/// </summary>
public sealed class CatalogShapeEquivalenceTests
{
    private const int HostileLength = 100_000;
    private const int VeryHostileLength = 1_000_000;

    private static readonly Regex SuiteSlug = new("^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$", RegexOptions.CultureInvariant);
    private static readonly Regex SuiteRef = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex VariantKey = new("^[A-Za-z_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex CommitSha = new("^[0-9a-f]{7,64}$", RegexOptions.CultureInvariant);
    private static readonly CreateSuiteCommandValidator SuiteValidator = new();

    [Fact]
    public void SuiteSlug_OldPatternAndValidator_AgreeOnEveryInput()
    {
        ShapesAgree(SuiteSlug, SuiteSlugAccepted, Corpus());
    }

    [Fact]
    public void SuiteRef_OldPatternAndPlanner_AgreeOnEveryInput()
    {
        ShapesAgree(SuiteRef, SuiteRefAccepted, Corpus());
    }

    [Fact]
    public void VariantKey_OldPatternAndNormalizer_AgreeOnEveryInput()
    {
        ShapesAgree(VariantKey, VariantKeyAccepted, Corpus());
    }

    [Fact]
    public void CommitSha_OldPatternAndShapeHelper_AgreeOnEveryInput()
    {
        // Both commit-sha sites (the sync and the revision store) call AsciiShape.IsMatch with exactly these bounds.
        ShapesAgree(CommitSha, CommitShaAccepted, Corpus());
    }

    [Fact]
    public void TrailingNewline_EveryShape_IsAcceptedByTheOldPatternAndRefusedNow()
    {
        const string slugWithNewline = "ab\n";
        const string refWithNewline = "a\n";
        const string keyWithNewline = "a\n";
        const string shaWithNewline = "abcdef1\n";

        SuiteSlug.IsMatch(slugWithNewline).ShouldBeTrue();
        SuiteSlugAccepted(slugWithNewline).ShouldBeFalse();

        SuiteRef.IsMatch(refWithNewline).ShouldBeTrue();
        SuiteRefAccepted(refWithNewline).ShouldBeFalse();

        VariantKey.IsMatch(keyWithNewline).ShouldBeTrue();
        VariantKeyAccepted(keyWithNewline).ShouldBeFalse();

        CommitSha.IsMatch(shaWithNewline).ShouldBeTrue();
        CommitShaAccepted(shaWithNewline).ShouldBeFalse();

        // A newline anywhere else was refused before and is refused now.
        SuiteSlug.IsMatch("a\nb").ShouldBeFalse();
        SuiteSlugAccepted("a\nb").ShouldBeFalse();
        SuiteRef.IsMatch("\na").ShouldBeFalse();
        SuiteRefAccepted("\na").ShouldBeFalse();
        VariantKey.IsMatch("a\nb").ShouldBeFalse();
        VariantKeyAccepted("a\nb").ShouldBeFalse();
        CommitSha.IsMatch("abcdef1\n0").ShouldBeFalse();
        CommitShaAccepted("abcdef1\n0").ShouldBeFalse();
    }

    [Fact]
    public void HostileInput_EveryShape_RefusesWithinOneSecond()
    {
        string[] hostile =
        [
            new string('a', HostileLength),
            new string('a', VeryHostileLength),
            new string('-', VeryHostileLength),
            new string('a', VeryHostileLength) + "\n",
        ];

        foreach (var (name, accepted) in Shapes())
        {
            foreach (var value in hostile)
            {
                var stopwatch = Stopwatch.StartNew();
                var result = accepted(value);
                stopwatch.Stop();

                result.ShouldBeFalse($"{name} accepted a {value.Length}-character input");
                stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1), $"{name} took {stopwatch.Elapsed} to refuse {value.Length} characters");
            }
        }
    }

    private static (string Name, Func<string, bool> Accepted)[] Shapes() =>
    [
        ("suite slug", SuiteSlugAccepted),
        ("suite ref", SuiteRefAccepted),
        ("variant key", VariantKeyAccepted),
        ("commit sha", CommitShaAccepted),
    ];

    private static bool SuiteSlugAccepted(string value) =>
        SuiteValidator.Validate(new CreateSuiteCommand(Guid.CreateVersion7(), "suite", value, "idempotency-key")).IsValid;

    private static bool SuiteRefAccepted(string value) => SuiteRevisionPlanner.IsValidRef(value);

    private static bool VariantKeyAccepted(string value) => VariantNormalizer.IsValidKey(value);

    private static bool CommitShaAccepted(string value) => AsciiShape.IsMatch(value, 7, 64, AsciiShape.LowerHex, AsciiShape.LowerHex);

    /// <summary>Values every shape sees: valid samples, each boundary length, the characters the patterns refuse, lookalikes and control characters.</summary>
    private static string[] Corpus() =>
    [
        string.Empty,
        "a", "b", "0", "9", "A", "_", ".", "-", "a-b", "-a", "a-", "-a-", "a--b", "ab", "0a", "a0", "_a", ".a", "a_", "a.",
        "abc", "ABC", "Abc", "a_b", "A_b.c-d", "a.b_c", "0abcdef", "abcdef0", "0123456789abcdef",
        "ab c", " ab", "ab ", "\tab", "ab\t", "\0", "a\0b", "a\u0001b", "a\tb", "a\rb", "a\r\nb",
        "\nab", "a\nb", "ab\n", "a\n", "abcdef1\n", new string('a', 64) + "\n",
        "\uFF41bc", "a\u0430bc", "AB\u0421", "abc\uFF11", "\u00E9", "a\u00E9",
        new string('a', 6), new string('a', 7), new string('a', 8),
        new string('f', 7), new string('f', 64), new string('F', 64), new string('g', 64),
        new string('a', 62), new string('a', 63), new string('a', 64), new string('a', 65),
        new string('-', 62), new string('-', 63), new string('-', 64), new string('-', 65),
        new string('a', 62) + "-", new string('a', 63) + "-", new string('a', 62) + "\0",
        new string('a', HostileLength),
    ];

    private static void ShapesAgree(Regex oracle, Func<string, bool> production, string[] corpus)
    {
        var differences = new List<string>();
        var trailingNewlineCases = 0;

        foreach (var value in corpus)
        {
            var expected = oracle.IsMatch(value);
            var actual = production(value);

            if (expected && value.EndsWith('\n'))
            {
                actual.ShouldBeFalse($"{Describe(value)}: the helper must refuse a trailing newline");
                trailingNewlineCases++;
                continue;
            }

            if (expected != actual)
            {
                differences.Add($"{Describe(value)}: old={expected} new={actual}");
            }
        }

        corpus.Length.ShouldBeGreaterThan(50);
        trailingNewlineCases.ShouldBeGreaterThan(0, "the corpus must exercise the documented trailing-newline difference");
        differences.ShouldBeEmpty();
    }

    private static string Describe(string value) =>
        value.Length <= 64
            ? "\"" + value.Replace("\0", "\\0", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\""
            : $"{value.Length}-character input";
}
