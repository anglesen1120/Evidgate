using System.Text.RegularExpressions;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>
/// The pattern language of binding allowlists: literal text where <c>*</c> matches any run of
/// characters (including none and including <c>/</c>) and nothing else is special. Matching is
/// anchored and ordinal, so <c>refs/heads/main</c> never matches <c>refs/heads/main2</c>.
/// </summary>
internal static class RefGlob
{
    /// <summary>True when <paramref name="value"/> matches <paramref name="pattern"/>.</summary>
    /// <param name="pattern">An allowlist entry.</param>
    /// <param name="value">The claim to test.</param>
    public static bool IsMatch(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);

        // Linear two-pointer wildcard match: no regex, so no backtracking blow-up on a hostile pattern.
        int p = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (p < pattern.Length && pattern[p] == value[v])
            {
                p++;
                v++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>True when any entry of <paramref name="patterns"/> matches.</summary>
    /// <param name="patterns">The allowlist.</param>
    /// <param name="value">The claim.</param>
    public static bool IsAnyMatch(IEnumerable<string> patterns, string value) =>
        patterns.Any(pattern => IsMatch(pattern, value));
}

/// <summary>Syntax of what an admin may put into a binding (shape validation, 00 §5.7 S3).</summary>
internal static partial class CiBindingShapes
{
    public const int MaxEntries = 32;

    /// <summary>A git ref pattern: <c>refs/heads/…</c> or <c>refs/tags/…</c>, <c>*</c> allowed.</summary>
    public static bool IsRefPattern(string value) => RefPattern().IsMatch(value);

    /// <summary>A workflow ref pattern: <c>owner/repo/.github/workflows/file.yml@refs/…</c>, <c>*</c> allowed.</summary>
    public static bool IsWorkflowPattern(string value) => WorkflowPattern().IsMatch(value);

    /// <summary><c>owner/repo</c> as GitHub spells it.</summary>
    public static bool IsRepositoryFullName(string value) => FullName().IsMatch(value);

    [GeneratedRegex(@"^refs/(heads|tags)/[A-Za-z0-9._/*+@-]{1,200}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RefPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._*-]{1,100}/[A-Za-z0-9._*-]{1,100}/\.github/workflows/[A-Za-z0-9._*-]{1,100}@refs/(heads|tags)/[A-Za-z0-9._/*+@-]{1,200}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex WorkflowPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}/[A-Za-z0-9._-]{1,100}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex FullName();
}
