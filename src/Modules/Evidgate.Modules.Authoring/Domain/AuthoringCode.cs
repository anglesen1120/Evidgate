using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Evidgate.Modules.Authoring.Domain;

/// <summary>
/// A manual test case's code (13.1.1 AC4, §4): <c>&lt;PREFIX&gt;-&lt;n&gt;</c> — the project's stored
/// <c>case_prefix</c>, a hyphen, and a number formatted with at least three digits (<c>SHOP-007</c>,
/// <c>SHOP-1000</c>). The code is allocated once inside the creating transaction, is unique per project and is
/// immutable afterwards, so the value object is the only place that formats or parses one.
/// </summary>
internal sealed partial record AuthoringCode
{
    /// <summary>
    /// The format check of <c>manual_test_case.code</c> (§4's grammar). Anchored with <c>\A</c>/<c>\z</c> rather than
    /// <c>^</c>/<c>$</c>: .NET's <c>$</c> matches before a trailing newline, so <c>"SHOP-007\n"</c> would pass the plan's
    /// written form — and a code is an identifier that reaches URLs and the wire unchanged.
    /// </summary>
    public const string FormatPattern = @"\A[A-Z][A-Z0-9]{1,7}-[0-9]{3,10}\z";

    /// <summary>The grammar of <c>project.case_prefix</c> (OI-13-101, approved 2026-10-23), anchored the same way.</summary>
    public const string PrefixPattern = @"\A[A-Z][A-Z0-9]{1,7}\z";

    /// <summary>One project can allocate at most this many codes; beyond it the allocator answers <c>409 quota_exceeded</c> (§4).</summary>
    public const long MaximumNumber = 1_000_000_000;

    /// <summary>The minimum number of digits of the numeric half (§4: <c>007</c>).</summary>
    public const int MinimumDigits = 3;

    private AuthoringCode(string prefix, long number)
    {
        Prefix = prefix;
        Number = number;
    }

    /// <summary>The project prefix, <c>^[A-Z][A-Z0-9]{1,7}$</c>.</summary>
    public string Prefix { get; }

    /// <summary>The allocated number, 1..<see cref="MaximumNumber"/>.</summary>
    public long Number { get; }

    /// <summary>The wire form, <c>&lt;PREFIX&gt;-&lt;n padded to three digits&gt;</c>.</summary>
    public string Value => Prefix + "-" + Number.ToString("D" + MinimumDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="prefix"/> is a legal <c>case_prefix</c>.</summary>
    /// <param name="prefix">The candidate.</param>
    public static bool IsValidPrefix(string? prefix) =>
        !string.IsNullOrEmpty(prefix) && PrefixRegex().IsMatch(prefix);

    /// <summary>
    /// Builds the code of an allocated number. Returns false for a number outside 1..<see cref="MaximumNumber"/> or an
    /// illegal prefix, so the allocator can answer <c>quota_exceeded</c> instead of writing a code the format check refuses.
    /// </summary>
    /// <param name="prefix">The project's stored prefix.</param>
    /// <param name="number">The allocated number.</param>
    /// <param name="code">The code, when both parts are legal.</param>
    public static bool TryFromNumber(string? prefix, long number, [NotNullWhen(true)] out AuthoringCode? code)
    {
        if (!IsValidPrefix(prefix) || number < 1 || number > MaximumNumber)
        {
            code = null;
            return false;
        }

        code = new AuthoringCode(prefix!, number);
        return true;
    }

    /// <summary>Parses a stored code strictly; a row that does not match is a bug, not a caller error.</summary>
    /// <param name="text">The stored code.</param>
    /// <param name="code">The parsed value, when the text matches <see cref="FormatPattern"/>.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out AuthoringCode? code)
    {
        code = null;

        if (string.IsNullOrEmpty(text) || !CodeRegex().IsMatch(text))
        {
            return false;
        }

        var separator = text.IndexOf('-', StringComparison.Ordinal);
        var prefix = text[..separator];
        var number = long.Parse(text[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);

        if (!IsValidPrefix(prefix) || number < 1 || number > MaximumNumber)
        {
            return false;
        }

        code = new AuthoringCode(prefix, number);
        return true;
    }

    /// <summary>The wire form, so an interpolated code is the stored code.</summary>
    public override string ToString() => Value;

    [GeneratedRegex(FormatPattern, RegexOptions.CultureInvariant, 100)]
    private static partial Regex CodeRegex();

    [GeneratedRegex(PrefixPattern, RegexOptions.CultureInvariant, 100)]
    private static partial Regex PrefixRegex();
}
