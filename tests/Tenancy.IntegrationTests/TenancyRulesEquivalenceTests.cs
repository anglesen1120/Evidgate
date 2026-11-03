using System.Globalization;
using System.Text.RegularExpressions;
using Evidgate.Modules.Tenancy.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The request-path shapes of the registry routes against the patterns they replaced: the
/// old <c>^…$</c> slug and domain anchors, and the <c>\z</c> case prefix. A trailing newline
/// that <c>$</c> let through is the one documented difference, asserted apart from the rest.
/// </summary>
public sealed class TenancyRulesEquivalenceTests
{
    private const int MaxDomainLength = 253;

    // The oracles keep the old patterns verbatim and carry NO timeout: a timeout would add a
    // third outcome the production helper does not have and hide a real equivalence break.
    private static readonly Regex SlugOracle =
        new("^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$", RegexOptions.CultureInvariant);

    private static readonly Regex CasePrefixOracle =
        new(@"^[A-Z][A-Z0-9]{1,7}\z", RegexOptions.CultureInvariant);

    private static readonly Regex DomainOracle =
        new(
            @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$",
            RegexOptions.CultureInvariant);

    // 'a', 'Z', '0', '-', '.', newline, NUL, Cyrillic 'а' (U+0430) and fullwidth 'ａ' (U+FF41).
    private static readonly char[] Alphabet = ['a', 'Z', '0', '-', '.', '\n', '\0', '\u0430', '\uFF41'];

    private static readonly int[] BoundaryLengths = [1, 2, 3, 8, 9, 62, 63, 64, 253, 254];

    // Lengths where a 64-character group of label plus dot leaves a one-character remainder.
    private static readonly int[] DomainSplitLengths = [64, 65, 127, 128, 129, 191, 192, 255];

    private static readonly string[] ExplicitValues =
    [
        string.Empty, "a", "ab", "a-b", "ab-", "-ab", "a--b", "AB", "Ab", "aB",
        "\u0430b", "\uFF41b", "ab\0", "ab\n", "ab\n\n", "\nab", "acme.example", "acme.",
        ".example", "acme..example", "-acme.example", "acme-.example", "acme.-example",
        "A.example", "acme.Example", "\u0430cme.example", "\uFF41cme.example", "acme.example\n",
        "ab\t", "\tab", "ab\r", "ab\0", "acme.example\0", "acme.example\t",
        "SH", "SHOP", "SHOP1234", "SHOP12345", "S", "SHOP\n", "\u0410B", "\uFF21B", "SHOP-1", "1SHOP",
        "checkout.example.com", "a.b", "0.0", "a-.b", "a.-b",
    ];

    [Fact]
    public void Slug_MatchesTheOldPattern_ExceptForATrailingNewline()
    {
        var corpus = Corpus().ToList();
        corpus.Count.ShouldBeGreaterThanOrEqualTo(820);
        corpus.ShouldContain("ab");
        corpus.ShouldContain("-ab");

        var (documented, unexpected) = Compare(TenancyRules.IsSlug, value => SlugOracle.IsMatch(value));

        TenancyRules.IsSlug("ab").ShouldBeTrue();
        TenancyRules.IsSlug("-ab").ShouldBeFalse();
        unexpected.ShouldBeEmpty(string.Join(", ", unexpected));
        documented.ShouldBeGreaterThan(0, "the old `$` anchor accepted 'ab\\n'");
    }

    [Fact]
    public void CasePrefix_MatchesTheOldPattern_WithNoDifference()
    {
        var corpus = Corpus().ToList();
        corpus.Count.ShouldBeGreaterThanOrEqualTo(820);
        corpus.ShouldContain("AB");
        corpus.ShouldContain("SHOP1234");

        var (documented, unexpected) = Compare(TenancyRules.IsCasePrefix, value => CasePrefixOracle.IsMatch(value));

        TenancyRules.IsCasePrefix("AB").ShouldBeTrue();
        TenancyRules.IsCasePrefix("SHOP12345").ShouldBeFalse();
        unexpected.ShouldBeEmpty(string.Join(", ", unexpected));
        documented.ShouldBe(0, "the case prefix already anchored with \\z");
    }

    [Fact]
    public void Domain_MatchesTheOldPattern_ExceptForATrailingNewline()
    {
        var corpus = Corpus().ToList();
        corpus.Count.ShouldBeGreaterThanOrEqualTo(820);
        corpus.ShouldContain("acme.example");

        var (documented, unexpected) = Compare(
            TenancyRules.IsDomain,
            value => value.Length <= MaxDomainLength && DomainOracle.IsMatch(value));

        TenancyRules.IsDomain("acme.example").ShouldBeTrue();
        TenancyRules.IsDomain("acme").ShouldBeFalse();
        unexpected.ShouldBeEmpty(string.Join(", ", unexpected));
        documented.ShouldBeGreaterThan(0, "the old `$` anchor accepted 'acme.example\\n'");
    }

    [Fact]
    public void TrailingNewline_IsRefusedByTheShape_AndAcceptedByTheOldAnchors()
    {
        SlugOracle.IsMatch("ab\n").ShouldBeTrue();
        TenancyRules.IsSlug("ab\n").ShouldBeFalse();

        DomainOracle.IsMatch("acme.example\n").ShouldBeTrue();
        TenancyRules.IsDomain("acme.example\n").ShouldBeFalse();

        CasePrefixOracle.IsMatch("AB\n").ShouldBeFalse();
        TenancyRules.IsCasePrefix("AB\n").ShouldBeFalse();

        TenancyRules.IsSlug("ab").ShouldBeTrue();
        TenancyRules.IsDomain("acme.example").ShouldBeTrue();
        TenancyRules.IsCasePrefix("AB").ShouldBeTrue();
    }

    [Fact]
    public void DomainOfLength_EveryBoundaryLength_IsEmittedExactly()
    {
        foreach (var length in BoundaryLengths.Concat(DomainSplitLengths))
        {
            DomainOfLength(length).Length.ShouldBe(length);
        }

        DomainOfLength(64).ShouldContain(".");
        DomainOfLength(128).ShouldContain(".");
    }

    [Fact]
    public void Null_IsRefusedByEveryShape()
    {
        TenancyRules.IsSlug(null).ShouldBeFalse();
        TenancyRules.IsCasePrefix(null).ShouldBeFalse();
        TenancyRules.IsDomain(null).ShouldBeFalse();
    }

    /// <summary>Counts the documented newline class and collects every other disagreement.</summary>
    private static (int Documented, string[] Unexpected) Compare(
        Func<string?, bool> shape,
        Func<string, bool> oracle)
    {
        var documented = 0;
        var unexpected = new List<string>();

        foreach (var value in Corpus())
        {
            var expected = oracle(value);
            var actual = shape(value);
            if (expected == actual)
            {
                continue;
            }

            // Only this class may differ: `$` also matched before a single final newline.
            if (expected && !actual && value.EndsWith('\n') && shape(value[..^1]))
            {
                documented++;
                continue;
            }

            unexpected.Add(Describe(value));
        }

        return (documented, [.. unexpected]);
    }

    private static IEnumerable<string> Corpus()
    {
        for (var length = 0; length <= 3; length++)
        {
            foreach (var value in Words(length))
            {
                yield return value;
            }
        }

        foreach (var value in BoundaryValues())
        {
            yield return value;
        }

        foreach (var value in ExplicitValues)
        {
            yield return value;
        }
    }

    /// <summary>Every string of <paramref name="length"/> characters over <see cref="Alphabet"/>.</summary>
    private static IEnumerable<string> Words(int length, string prefix = "")
    {
        if (length == 0)
        {
            yield return prefix;
            yield break;
        }

        foreach (var character in Alphabet)
        {
            foreach (var value in Words(length - 1, prefix + character))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> BoundaryValues()
    {
        foreach (var length in BoundaryLengths)
        {
            var letters = new string('a', length);
            yield return letters;
            yield return letters + "-";
            yield return "-" + letters;
            yield return new string('0', length);
            yield return new string('A', length);
            yield return DomainOfLength(length);
        }

        // Domains of exactly 253 and 254 characters, one character over the caller's cap.
        yield return DomainOfLength(253);
        yield return DomainOfLength(254);
        yield return DomainOfLength(253) + ".b";
        yield return DomainOfLength(60) + ".";
        yield return "." + DomainOfLength(60);

        foreach (var length in DomainSplitLengths)
        {
            yield return DomainOfLength(length);
        }

        // One DNS label at 63 and 64 characters.
        yield return new string('a', 63) + ".b";
        yield return new string('a', 64) + ".b";
        yield return "a." + new string('a', 63) + ".b";
        yield return "a." + new string('a', 62) + ".b";
    }

    /// <summary>An all-'a' domain of exactly <paramref name="length"/> characters.</summary>
    private static string DomainOfLength(int length)
    {
        var labels = new List<string>();
        var remaining = length;

        while (remaining > 0)
        {
            // Never leave a single character behind a dot: it would be spent on the
            // separator and the emitted value would end up one character short.
            var size = Math.Min(63, remaining);
            if (remaining - size == 1)
            {
                size--;
            }

            labels.Add(new string('a', size));
            remaining -= size;

            if (remaining > 0)
            {
                remaining--;
            }
        }

        return string.Join('.', labels);
    }

    /// <summary>Escaped, bounded text so a failure can be reproduced from an odd character.</summary>
    private static string Describe(string value)
    {
        var escaped = value.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\0", "\\0", StringComparison.Ordinal);
        return value.Length <= 32
            ? $"'{escaped}'"
            : string.Create(CultureInfo.InvariantCulture, $"'{escaped[..32]}…' (length {value.Length})");
    }
}
